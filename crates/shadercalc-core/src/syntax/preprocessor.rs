use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use super::lexer::{Token, TokenKind, tokenize};
use crate::diagnostics::{Diagnostic, DiagnosticBag, DiagnosticSeverity, SourceSpan};

#[derive(Clone, Debug)]
struct Macro {
    name: String,
    parameters: Option<Vec<String>>,
    is_variadic: bool,
    body: Vec<Token>,
}

/// A `#define` the files declared (and didn't `#undef`): its file and the span of the whole directive.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MacroDefinition {
    pub name: String,
    pub parameters: Option<Vec<String>>,
    pub source: String,
    pub span: SourceSpan,
}

/// C preprocessor subset: #define (object-like and function-like, # and ##), #undef, #if/#ifdef/#ifndef/#elif/
/// #else/#endif, #error. #include, #pragma and #line are ignored. Expanded tokens keep the invocation's position.
pub struct Preprocessor {
    macros: HashMap<String, Macro>,
    source_name: String,
    diagnostics: DiagnosticBag,
    /// The `#define`s of the files processed, in order.
    pub definitions: Vec<MacroDefinition>,
}

impl Preprocessor {
    fn new(source_name: &str) -> Preprocessor {
        let mut macros: HashMap<String, Macro> = HashMap::new();
        for (name, value) in [
            ("__HLSL_VERSION", "2021"),
            ("__SHADER_TARGET_MAJOR", "6"),
            ("__SHADER_TARGET_MINOR", "0"),
            ("__SHADER_TARGET_STAGE", "5"),
        ] {
            let body: Vec<Token> = vec![Token::new(TokenKind::Number, value, SourceSpan::NONE, false, true)];
            macros
                .insert(name.to_string(), Macro { name: name.to_string(), parameters: None, is_variadic: false, body });
        }
        Preprocessor {
            macros,
            source_name: source_name.to_string(),
            diagnostics: DiagnosticBag::new(),
            definitions: Vec::new(),
        }
    }

    pub fn process(tokens: Vec<Token>, source_name: &str, diagnostics: &mut DiagnosticBag) -> Vec<Token> {
        Preprocessor::new(source_name).process_file(tokens, source_name, diagnostics)
    }

    /// A preprocessor whose macros carry over from one file to the next (worksheet tabs, like headers).
    pub fn create_shared() -> Preprocessor {
        Preprocessor::new("")
    }

    pub fn process_file(
        &mut self,
        tokens: Vec<Token>,
        source_name: &str,
        diagnostics: &mut DiagnosticBag,
    ) -> Vec<Token> {
        self.source_name = source_name.to_string();
        let output: Vec<Token> = self.run(tokens);
        let collected: DiagnosticBag = std::mem::take(&mut self.diagnostics);
        diagnostics.add_range(collected.items());
        output
    }

    fn run(&mut self, tokens: Vec<Token>) -> Vec<Token> {
        let mut output: Vec<Token> = Vec::new();
        let mut text: Vec<Token> = Vec::new();
        // Each entry: is this region active, has any branch of the #if been taken, was the parent active
        let mut conditions: Vec<(bool, bool, bool)> = Vec::new();
        let mut active: bool = true;

        let mut index: usize = 0;
        while index < tokens.len() && tokens[index].kind != TokenKind::End {
            let token: &Token = &tokens[index];
            if !(token.starts_line && token.is("#")) {
                if active {
                    text.push(token.clone());
                }
                index += 1;
                continue;
            }

            // A directive runs to the end of its line
            let mut end: usize = index + 1;
            while !tokens[end].starts_line {
                end += 1;
            }
            let line: Vec<Token> = tokens[index + 1..end].to_vec();
            index = end;
            if line.is_empty() {
                continue;
            }

            let expanded: Vec<Token> = self.expand(std::mem::take(&mut text));
            output.extend(expanded);
            let directive: String = line[0].text.clone();
            match directive.as_str() {
                "if" | "ifdef" | "ifndef" => {
                    let condition: bool = active
                        && if directive == "if" {
                            self.evaluate_condition(&line)
                        } else {
                            let name: String = self.name_at(&line, 1);
                            self.macros.contains_key(&name) == (directive == "ifdef")
                        };
                    conditions.push((condition, condition, active));
                    active = condition;
                }
                "elif" | "else" => {
                    let Some((_, taken, parent_active)) = conditions.pop() else {
                        self.report(&line[0], format!("#{directive} without #if"));
                        continue;
                    };
                    let condition: bool =
                        parent_active && !taken && (directive == "else" || self.evaluate_condition(&line));
                    conditions.push((condition, taken || condition, parent_active));
                    active = condition;
                }
                "endif" => match conditions.pop() {
                    Some((_, _, parent_active)) => active = parent_active,
                    None => self.report(&line[0], "#endif without #if".to_string()),
                },
                "define" if active => self.define(&line, token.span),
                "undef" if active => {
                    let name: String = self.name_at(&line, 1);
                    self.macros.remove(&name);
                    self.definitions.retain(|definition| definition.name != name);
                }
                "error" if active => {
                    let message: Vec<&str> = line.iter().skip(1).map(|part| part.text.as_str()).collect();
                    self.report(&line[0], format!("#error {}", message.join(" ")));
                }
                "include" if active => {
                    self.diagnostics.add(Diagnostic::new(
                        DiagnosticSeverity::Info,
                        "#include is ignored: paste the code it needs",
                        self.source_name.clone(),
                        line[0].span,
                    ));
                }
                "pragma" | "line" | "define" | "undef" | "error" | "include" => {}
                _ => {
                    if active {
                        self.report(&line[0], format!("Unknown directive #{directive}"));
                    }
                }
            }
        }

        let last: Token = tokens.last().expect("token lists end with End").clone();
        if !conditions.is_empty() {
            self.report(&last, "Missing #endif".to_string());
        }
        let expanded: Vec<Token> = self.expand(text);
        output.extend(expanded);
        output.push(last);
        output
    }

    fn name_at(&mut self, line: &[Token], position: usize) -> String {
        if position < line.len() && line[position].kind == TokenKind::Identifier {
            return line[position].text.clone();
        }
        self.report(&line[0], format!("#{} needs a macro name", line[0].text));
        String::new()
    }

    fn define(&mut self, line: &[Token], hash: SourceSpan) {
        let name: String = self.name_at(line, 1);
        if name.is_empty() {
            return;
        }
        let mut body_start: usize = 2;
        let mut parameters: Option<Vec<String>> = None;
        let mut is_variadic: bool = false;
        // Function-like only when '(' touches the name
        if line.len() > 2 && line[2].is("(") && !line[2].has_leading_space {
            let mut names: Vec<String> = Vec::new();
            let mut position: usize = 3;
            while position < line.len() && !line[position].is(")") {
                if line[position].is("...") {
                    is_variadic = true;
                    names.push("__VA_ARGS__".to_string());
                } else if line[position].kind == TokenKind::Identifier {
                    names.push(line[position].text.clone());
                }
                position += 1;
            }
            parameters = Some(names);
            body_start = position + 1;
        }
        let body: Vec<Token> = line.iter().skip(body_start).cloned().collect();
        self.definitions.retain(|definition| definition.name != name);
        self.definitions.push(MacroDefinition {
            name: name.clone(),
            parameters: parameters.clone(),
            source: self.source_name.clone(),
            span: hash.to(line[line.len() - 1].span),
        });
        self.macros.insert(name.clone(), Macro { name, parameters, is_variadic, body });
    }

    // ---- Expansion ----

    fn expand(&mut self, input: Vec<Token>) -> Vec<Token> {
        let mut output: Vec<Token> = Vec::new();
        // Pending tokens, last one first: expansions are pushed back and rescanned
        let mut pending: Vec<Token> = input;
        pending.reverse();
        while let Some(token) = pending.pop() {
            let found: Option<Macro> =
                if token.kind == TokenKind::Identifier { self.macros.get(&token.text).cloned() } else { None };
            let Some(definition) = found else {
                output.push(token);
                continue;
            };
            if token.hidden.as_ref().is_some_and(|hidden| hidden.contains(&definition.name)) {
                output.push(token);
                continue;
            }

            let mut arguments: Option<Vec<Vec<Token>>> = None;
            if let Some(parameters) = &definition.parameters {
                if pending.last().is_none_or(|next| !next.is("(")) {
                    output.push(token);
                    continue;
                }
                pending.pop();
                let mut collected: Vec<Vec<Token>> = self.collect_arguments(&mut pending, &token);
                if collected.len() == 1 && collected[0].is_empty() && parameters.is_empty() {
                    collected.clear();
                }
                if collected.len() != parameters.len()
                    && !(definition.is_variadic && collected.len() + 1 >= parameters.len())
                {
                    self.report(
                        &token,
                        format!(
                            "Macro '{}' takes {} arguments, got {}",
                            definition.name,
                            parameters.len(),
                            collected.len()
                        ),
                    );
                    continue;
                }
                arguments = Some(collected);
            }

            let mut hidden: HashSet<String> = token.hidden.as_deref().cloned().unwrap_or_default();
            hidden.insert(definition.name.clone());
            let hidden: Arc<HashSet<String>> = Arc::new(hidden);
            // The expansion starts where the invocation did (worksheet lines end at line starts)
            let replacement: Vec<Token> = self.substitute(&definition, arguments.as_ref(), &token);
            for (position, part) in replacement.into_iter().enumerate().rev() {
                pending.push(Token {
                    span: token.span,
                    hidden: Some(hidden.clone()),
                    starts_line: position == 0 && token.starts_line,
                    ..part
                });
            }
        }
        output
    }

    fn collect_arguments(&mut self, pending: &mut Vec<Token>, invocation: &Token) -> Vec<Vec<Token>> {
        let mut arguments: Vec<Vec<Token>> = vec![Vec::new()];
        let mut depth: i32 = 0;
        loop {
            let Some(token) = pending.pop() else {
                self.report(invocation, format!("Unterminated call of macro '{}'", invocation.text));
                return arguments;
            };
            if token.is("(") || token.is("[") || token.is("{") {
                depth += 1;
            } else if token.is(")") || token.is("]") || token.is("}") {
                if depth == 0 && token.is(")") {
                    return arguments;
                }
                depth -= 1;
            } else if depth == 0 && token.is(",") {
                arguments.push(Vec::new());
                continue;
            }
            arguments.last_mut().expect("at least one argument").push(token);
        }
    }

    fn substitute(
        &mut self,
        definition: &Macro,
        arguments: Option<&Vec<Vec<Token>>>,
        invocation: &Token,
    ) -> Vec<Token> {
        let body: &Vec<Token> = &definition.body;
        let mut result: Vec<Token> = Vec::new();
        let mut position: usize = 0;
        while position < body.len() {
            let token: &Token = &body[position];
            let parameter: Option<usize> = parameter_index(definition, token);
            // #param: the argument as a string
            if let Some(arguments) = arguments {
                if token.is("#")
                    && position + 1 < body.len()
                    && let Some(stringized) = parameter_index(definition, &body[position + 1])
                {
                    position += 1;
                    let argument: Vec<Token> = argument_tokens(definition, arguments, stringized);
                    let text: Vec<&str> = argument.iter().map(|part| part.text.as_str()).collect();
                    let quoted: String = format!("\"{}\"", text.join(" ").replace('"', "\\\""));
                    result.push(Token::new(TokenKind::String, quoted, invocation.span, false, true));
                    position += 1;
                    continue;
                }
                if let Some(parameter) = parameter {
                    let is_pasted: bool = (position > 0 && body[position - 1].is("##"))
                        || (position + 1 < body.len() && body[position + 1].is("##"));
                    let argument: Vec<Token> = argument_tokens(definition, arguments, parameter);
                    if is_pasted {
                        result.extend(argument);
                    } else {
                        let expanded: Vec<Token> = self.expand(argument);
                        result.extend(expanded);
                    }
                    position += 1;
                    continue;
                }
            }
            result.push(token.clone());
            position += 1;
        }

        // a ## b: glue the neighbours into one token
        let mut index: usize = 0;
        while index < result.len() {
            if !result[index].is("##") {
                index += 1;
                continue;
            }
            if index == 0 || index + 1 >= result.len() {
                result.remove(index);
                continue;
            }
            let glued: String = format!("{}{}", result[index - 1].text, result[index + 1].text);
            let relexed: Vec<Token> = tokenize(&glued, &self.source_name, &mut DiagnosticBag::new());
            let pasted: Token = if relexed.len() == 2 {
                Token { span: invocation.span, ..relexed[0].clone() }
            } else {
                result[index - 1].clone()
            };
            // Scanning resumes after the pasted token
            result.splice(index - 1..index + 2, [pasted]);
        }
        result
    }

    // ---- #if expressions ----

    fn evaluate_condition(&mut self, line: &[Token]) -> bool {
        // defined(X) / defined X are resolved before expansion
        let mut resolved: Vec<Token> = Vec::new();
        let mut position: usize = 1;
        while position < line.len() {
            if line[position].is("defined") {
                let has_parenthesis: bool = position + 1 < line.len() && line[position + 1].is("(");
                let name_position: usize = position + if has_parenthesis { 2 } else { 1 };
                let name: &str = if name_position < line.len() { &line[name_position].text } else { "" };
                let value: &str = if self.macros.contains_key(name) { "1" } else { "0" };
                resolved.push(Token::new(TokenKind::Number, value, line[position].span, false, true));
                position = name_position + if has_parenthesis { 1 } else { 0 } + 1;
                continue;
            }
            resolved.push(line[position].clone());
            position += 1;
        }

        let mut expanded: Vec<Token> = self.expand(resolved);
        expanded.push(Token::new(TokenKind::End, "", line[0].span, true, true));
        let mut cursor: usize = 0;
        match condition_expression(&expanded, &mut cursor, 0) {
            Ok(value) => value != 0,
            Err(message) => {
                self.report(&line[0], format!("Invalid #{} expression: {}", line[0].text, message));
                false
            }
        }
    }

    fn report(&mut self, token: &Token, message: String) {
        self.diagnostics.add(Diagnostic::new(DiagnosticSeverity::Error, message, self.source_name.clone(), token.span));
    }
}

fn parameter_index(definition: &Macro, token: &Token) -> Option<usize> {
    match &definition.parameters {
        Some(parameters) if token.kind == TokenKind::Identifier => {
            parameters.iter().position(|parameter| *parameter == token.text)
        }
        _ => None,
    }
}

fn argument_tokens(definition: &Macro, arguments: &[Vec<Token>], parameter: usize) -> Vec<Token> {
    let parameter_count: usize = definition.parameters.as_ref().map_or(0, Vec::len);
    if definition.is_variadic && parameter + 1 == parameter_count {
        // __VA_ARGS__: the remaining arguments, commas included
        let mut rest: Vec<Token> = Vec::new();
        for index in parameter..arguments.len() {
            if index > parameter {
                rest.push(Token::new(TokenKind::Punctuator, ",", SourceSpan::NONE, false, false));
            }
            rest.extend(arguments[index].iter().cloned());
        }
        return rest;
    }
    if parameter < arguments.len() { arguments[parameter].clone() } else { Vec::new() }
}

const CONDITION_LEVELS: [&[&str]; 10] = [
    &["||"],
    &["&&"],
    &["|"],
    &["^"],
    &["&"],
    &["==", "!="],
    &["<", ">", "<=", ">="],
    &["<<", ">>"],
    &["+", "-"],
    &["*", "/", "%"],
];

fn token_at(tokens: &[Token], cursor: usize) -> &Token {
    &tokens[cursor.min(tokens.len() - 1)]
}

fn condition_expression(tokens: &[Token], cursor: &mut usize, level: usize) -> Result<i64, String> {
    if level == 0 {
        let condition: i64 = condition_expression(tokens, cursor, 1)?;
        if token_at(tokens, *cursor).is("?") {
            *cursor += 1;
            let when_true: i64 = condition_expression(tokens, cursor, 0)?;
            expect(tokens, cursor, ":")?;
            let when_false: i64 = condition_expression(tokens, cursor, 0)?;
            return Ok(if condition != 0 { when_true } else { when_false });
        }
        return Ok(condition);
    }
    if level > CONDITION_LEVELS.len() {
        return condition_unary(tokens, cursor);
    }
    let mut left: i64 = condition_expression(tokens, cursor, level + 1)?;
    while token_at(tokens, *cursor).kind == TokenKind::Punctuator
        && CONDITION_LEVELS[level - 1].contains(&token_at(tokens, *cursor).text.as_str())
    {
        let operation: String = token_at(tokens, *cursor).text.clone();
        *cursor += 1;
        let right: i64 = condition_expression(tokens, cursor, level + 1)?;
        left = match operation.as_str() {
            "||" => (left != 0 || right != 0) as i64,
            "&&" => (left != 0 && right != 0) as i64,
            "|" => left | right,
            "^" => left ^ right,
            "&" => left & right,
            "==" => (left == right) as i64,
            "!=" => (left != right) as i64,
            "<" => (left < right) as i64,
            ">" => (left > right) as i64,
            "<=" => (left <= right) as i64,
            ">=" => (left >= right) as i64,
            "<<" => left.wrapping_shl(right as u32),
            ">>" => left.wrapping_shr(right as u32),
            "+" => left.wrapping_add(right),
            "-" => left.wrapping_sub(right),
            "*" => left.wrapping_mul(right),
            "/" if right == 0 => return Err("division by zero".to_string()),
            "/" => left.wrapping_div(right),
            _ if right == 0 => return Err("division by zero".to_string()),
            _ => left.wrapping_rem(right),
        };
    }
    Ok(left)
}

fn condition_unary(tokens: &[Token], cursor: &mut usize) -> Result<i64, String> {
    let token: &Token = token_at(tokens, *cursor);
    *cursor += 1;
    match token.text.as_str() {
        "!" => return Ok((condition_unary(tokens, cursor)? == 0) as i64),
        "~" => return Ok(!condition_unary(tokens, cursor)?),
        "-" => return Ok(condition_unary(tokens, cursor)?.wrapping_neg()),
        "+" => return condition_unary(tokens, cursor),
        "(" => {
            let inner: i64 = condition_expression(tokens, cursor, 0)?;
            expect(tokens, cursor, ")")?;
            return Ok(inner);
        }
        _ => {}
    }
    if token.kind == TokenKind::Number {
        let digits: &str = token.text.trim_end_matches(['u', 'U', 'l', 'L']);
        let parsed: Option<i64> = if digits.len() >= 2 && digits[..2].eq_ignore_ascii_case("0x") {
            i64::from_str_radix(&digits[2..], 16).ok()
        } else {
            digits.parse::<i64>().ok()
        };
        return parsed.ok_or_else(|| format!("invalid number '{}'", token.text));
    }
    // An identifier left after expansion counts as 0
    if token.kind == TokenKind::Identifier {
        return Ok(0);
    }
    Err(format!("unexpected '{}'", token.text))
}

fn expect(tokens: &[Token], cursor: &mut usize, text: &str) -> Result<(), String> {
    if !token_at(tokens, *cursor).is(text) {
        return Err(format!("expected '{text}'"));
    }
    *cursor += 1;
    Ok(())
}
