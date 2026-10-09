use std::collections::HashSet;

use super::lexer::{Token, TokenKind, tokenize};
use super::preprocessor::Preprocessor;
use super::tree::*;
use crate::diagnostics::{Diagnostic, DiagnosticBag, DiagnosticSeverity, SourceSpan};
use crate::types::ScalarKind;
use crate::units::{Dimension, UNITS, UnitDefinition};
use crate::values::scalars;

struct ParseError {
    message: String,
    span: SourceSpan,
}

type ParseResult<T> = Result<T, ParseError>;

const SKIPPED_MODIFIERS: [&str; 21] = [
    "inline",
    "constexpr",
    "extern",
    "export",
    "volatile",
    "precise",
    "FORCEINLINE",
    "FORCENOINLINE",
    "__forceinline",
    "linear",
    "centroid",
    "nointerpolation",
    "noperspective",
    "sample",
    "row_major",
    "column_major",
    "snorm",
    "unorm",
    "globallycoherent",
    "shared",
    "mutable",
];

const SCALAR_TYPE_NAMES: [&str; 21] = [
    "bool",
    "int",
    "uint",
    "dword",
    "half",
    "float",
    "double",
    "int64_t",
    "uint64_t",
    "int32_t",
    "uint32_t",
    "min16float",
    "min10float",
    "min16int",
    "min12int",
    "min16uint",
    "float32_t",
    "float64_t",
    "int16_t",
    "uint16_t",
    "float16_t",
];

const BINARY_LEVELS: [&[&str]; 10] = [
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

const ASSIGNMENT_OPERATORS: [&str; 11] = ["=", "+=", "-=", "*=", "/=", "%=", "<<=", ">>=", "&=", "|=", "^="];

const RESOURCE_TYPE_PREFIXES: [&str; 14] = [
    "Texture",
    "RWTexture",
    "Buffer",
    "RWBuffer",
    "StructuredBuffer",
    "RWStructuredBuffer",
    "ByteAddressBuffer",
    "RWByteAddressBuffer",
    "AppendStructuredBuffer",
    "ConsumeStructuredBuffer",
    "ConstantBuffer",
    "Sampler",
    "RaytracingAccelerationStructure",
    "FeedbackTexture",
];

fn is_skipped_modifier(text: &str) -> bool {
    SKIPPED_MODIFIERS.contains(&text)
}

fn is_integer_spelling(text: &str) -> bool {
    matches!(text, "unsigned" | "signed" | "long" | "short")
}

pub fn is_builtin_type_name(name: &str) -> bool {
    if SCALAR_TYPE_NAMES.contains(&name) {
        return true;
    }
    let scalar: &str = name.trim_end_matches(['1', '2', '3', '4', 'x']);
    if !SCALAR_TYPE_NAMES.contains(&scalar) {
        return false;
    }
    let suffix: &[u8] = &name.as_bytes()[scalar.len()..];
    let is_digit = |byte: u8| (b'1'..=b'4').contains(&byte);
    (suffix.len() == 1 && is_digit(suffix[0]))
        || (suffix.len() == 3 && is_digit(suffix[0]) && suffix[1] == b'x' && is_digit(suffix[2]))
}

/// Recursive-descent parser for HLSL (math subset) and the C++ forms people paste: const T& / T& parameters,
/// std:: names, static_cast, auto, inline/constexpr, namespaces, using-aliases, brace initialisation.
pub struct Parser<'a> {
    tokens: Vec<Token>,
    source_name: String,
    diagnostics: &'a mut DiagnosticBag,
    type_names: HashSet<String>,
    is_unit_name: Option<&'a dyn Fn(&str) -> bool>,
    position: usize,
    is_interactive: bool,
    /// Worksheet: a line break ends a top-level statement.
    is_line_mode: bool,
    /// Open brackets in the current expression, and braces of the current statement block.
    nesting: i32,
    block_depth: i32,
}

/// Parses pasted code: functions, structs, typedefs, globals.
pub fn parse_program(
    source: &str,
    source_name: &str,
    diagnostics: &mut DiagnosticBag,
    known_type_names: &[String],
) -> CompilationUnitSyntax {
    let lexed: Vec<Token> = tokenize(source, source_name, diagnostics);
    let tokens: Vec<Token> = Preprocessor::process(lexed, source_name, diagnostics);
    let mut parser: Parser = Parser::new(tokens, source_name, diagnostics, known_type_names, None);
    let mut declarations: Vec<DeclarationSyntax> = Vec::new();
    while !parser.is_end() {
        parser.parse_declarations(&mut declarations, false);
    }
    CompilationUnitSyntax { declarations }
}

/// Parses a calculator line: statements and declarations, the last expression may omit its ';', and
/// `number unit` is a unit literal.
pub fn parse_interactive(
    source: &str,
    source_name: &str,
    diagnostics: &mut DiagnosticBag,
    known_type_names: &[String],
    is_unit_name: &dyn Fn(&str) -> bool,
) -> Vec<ItemSyntax> {
    let lexed: Vec<Token> = tokenize(source, source_name, diagnostics);
    let tokens: Vec<Token> = Preprocessor::process(lexed, source_name, diagnostics);
    parse_items(tokens, source_name, diagnostics, known_type_names, is_unit_name, false)
}

/// Parses a worksheet file (tokens already preprocessed): declarations and calculation lines mixed, where a
/// line break ends a top-level statement unless a bracket is still open.
pub fn parse_worksheet(
    tokens: Vec<Token>,
    source_name: &str,
    diagnostics: &mut DiagnosticBag,
    known_type_names: &[String],
    is_unit_name: &dyn Fn(&str) -> bool,
) -> Vec<ItemSyntax> {
    parse_items(tokens, source_name, diagnostics, known_type_names, is_unit_name, true)
}

fn parse_items(
    tokens: Vec<Token>,
    source_name: &str,
    diagnostics: &mut DiagnosticBag,
    known_type_names: &[String],
    is_unit_name: &dyn Fn(&str) -> bool,
    is_line_mode: bool,
) -> Vec<ItemSyntax> {
    let mut parser: Parser = Parser::new(tokens, source_name, diagnostics, known_type_names, Some(is_unit_name));
    parser.is_interactive = true;
    parser.is_line_mode = is_line_mode;
    let mut items: Vec<ItemSyntax> = Vec::new();
    while !parser.is_end() {
        let start: usize = parser.position;
        parser.nesting = 0;
        parser.block_depth = 0;
        if let Err(error) = parser.parse_item(&mut items) {
            parser.report(error);
            parser.recover(start);
        }
    }
    items
}

impl<'a> Parser<'a> {
    fn new(
        tokens: Vec<Token>,
        source_name: &str,
        diagnostics: &'a mut DiagnosticBag,
        type_names: &[String],
        is_unit_name: Option<&'a dyn Fn(&str) -> bool>,
    ) -> Parser<'a> {
        Parser {
            tokens,
            source_name: source_name.to_string(),
            diagnostics,
            type_names: type_names.iter().cloned().collect(),
            is_unit_name,
            position: 0,
            is_interactive: false,
            is_line_mode: false,
            nesting: 0,
            block_depth: 0,
        }
    }

    /// The next token starts a new line where a worksheet statement may end.
    fn at_line_break(&self) -> bool {
        self.is_line_mode
            && self.nesting == 0
            && self.block_depth == 0
            && self.current().starts_line
            && self.current().kind != TokenKind::End
    }

    fn parse_item(&mut self, items: &mut Vec<ItemSyntax>) -> ParseResult<()> {
        if self.accept(";") {
            return Ok(());
        }
        if self.is_declaration_keyword() || self.is_function_start() || self.is_resource_declaration() {
            let mut declarations: Vec<DeclarationSyntax> = Vec::new();
            self.parse_declaration(&mut declarations)?;
            items.extend(declarations.into_iter().map(ItemSyntax::Declaration));
        } else if self.is_declaration_start() || self.is_statement_keyword() || self.is("{") {
            let statement: StatementSyntax = self.parse_statement()?;
            items.push(ItemSyntax::Statement(statement));
        } else {
            let first: SourceSpan = self.current().span;
            let expression: ExpressionSyntax = self.parse_expression()?;
            self.expect_statement_end()?;
            let span: SourceSpan = self.span_from(first);
            items.push(ItemSyntax::Statement(StatementSyntax::new(StatementKind::Expression(expression), span)));
        }
        Ok(())
    }

    /// `Texture2D T;`, `RWStructuredBuffer<float> B;`: skipped with a note (resources aren't supported).
    fn is_resource_declaration(&self) -> bool {
        self.current().kind == TokenKind::Identifier
            && !self.is_type_start(0)
            && ((self.peek(1).kind == TokenKind::Identifier && !self.peek(2).is("(") && !self.peek(1).starts_line)
                || (self.peek(1).is("<")
                    && RESOURCE_TYPE_PREFIXES.iter().any(|prefix| self.current().text.starts_with(prefix))))
    }

    // ---- Declarations ----

    fn parse_declarations(&mut self, declarations: &mut Vec<DeclarationSyntax>, inside_block: bool) {
        while !self.is_end() && !(inside_block && self.is("}")) {
            let start: usize = self.position;
            if let Err(error) = self.parse_declaration(declarations) {
                self.report(error);
                self.recover(start);
            }
        }
    }

    fn is_declaration_keyword(&self) -> bool {
        self.current().kind == TokenKind::Identifier
            && matches!(
                self.current().text.as_str(),
                "struct" | "typedef" | "using" | "namespace" | "cbuffer" | "tbuffer" | "template"
            )
    }

    fn parse_declaration(&mut self, declarations: &mut Vec<DeclarationSyntax>) -> ParseResult<()> {
        let start: SourceSpan = self.current().span;
        if self.accept(";") {
            return Ok(());
        }
        self.skip_attributes();
        if self.accept("namespace") {
            if self.current().kind == TokenKind::Identifier {
                self.advance();
            }
            self.expect("{")?;
            self.parse_declarations(declarations, true);
            self.expect("}")?;
            return Ok(());
        }
        if self.is("using") {
            return self.parse_using(declarations);
        }
        if self.accept("template") {
            return Err(self.error_at("Templates are not supported", start));
        }
        if self.is("struct") {
            let structure: StructSyntax = self.parse_struct()?;
            declarations.push(DeclarationSyntax::Struct(structure));
            return Ok(());
        }
        if self.accept("typedef") {
            self.skip_modifiers();
            let ty: TypeSyntax = self.parse_type()?;
            let name: Token = self.expect_identifier()?;
            let dimensions: Vec<Option<ExpressionSyntax>> = self.parse_array_dimensions()?;
            self.expect(";")?;
            self.type_names.insert(name.text.clone());
            let span: SourceSpan = self.span_from(start);
            declarations.push(DeclarationSyntax::Typedef(TypedefSyntax {
                ty,
                name: name.text,
                name_span: name.span,
                array_dimensions: dimensions,
                span,
            }));
            return Ok(());
        }
        if self.accept("cbuffer") || self.accept("tbuffer") {
            self.expect_identifier()?;
            self.skip_register();
            self.expect("{")?;
            while !self.accept("}") {
                let member: VariableDeclarationSyntax = self.parse_variable_declaration(StorageModifiers::UNIFORM)?;
                let span: SourceSpan = member.span;
                declarations
                    .push(DeclarationSyntax::GlobalVariable(GlobalVariableSyntax { declaration: member, span }));
            }
            self.accept(";");
            return Ok(());
        }

        let modifiers: StorageModifiers = self.skip_modifiers();
        // Resources (Texture2D T; RWStructuredBuffer<float> B;) often come with a pasted function: skip them
        if self.current().kind == TokenKind::Identifier
            && !self.is_type_start(0)
            && !self.is("void")
            && (self.peek(1).kind == TokenKind::Identifier || self.peek(1).is("<"))
            && !(self.peek(1).kind == TokenKind::Identifier && self.peek(2).is("("))
        {
            let resource: Token = self.current().clone();
            while !self.is_end() && !self.accept(";") {
                self.advance();
            }
            let span: SourceSpan = self.span_from(resource.span);
            self.diagnostics.add(Diagnostic::new(
                DiagnosticSeverity::Info,
                format!("'{}' declaration skipped: resources aren't supported", resource.text),
                self.source_name.clone(),
                span,
            ));
            return Ok(());
        }
        let return_or_variable_type: TypeSyntax = if self.is("void") {
            let text: String = self.advance().text;
            TypeSyntax::Named { name: text, span: self.previous().span }
        } else {
            self.parse_type()?
        };
        if self.current().kind == TokenKind::Identifier && self.peek(1).is("(") {
            let function: FunctionSyntax = self.parse_function(return_or_variable_type, start)?;
            declarations.push(DeclarationSyntax::Function(function));
            return Ok(());
        }
        let variables: VariableDeclarationSyntax = self.parse_declarators(modifiers, return_or_variable_type, start)?;
        let span: SourceSpan = variables.span;
        declarations.push(DeclarationSyntax::GlobalVariable(GlobalVariableSyntax { declaration: variables, span }));
        Ok(())
    }

    fn parse_using(&mut self, declarations: &mut Vec<DeclarationSyntax>) -> ParseResult<()> {
        let start: Token = self.expect("using")?;
        if self.accept("namespace") {
            while !self.is_end() && !self.accept(";") {
                self.advance();
            }
            return Ok(());
        }
        let name: Token = self.expect_identifier()?;
        self.expect("=")?;
        let ty: TypeSyntax = self.parse_type()?;
        self.expect(";")?;
        self.type_names.insert(name.text.clone());
        let span: SourceSpan = self.span_from(start.span);
        declarations.push(DeclarationSyntax::Typedef(TypedefSyntax {
            ty,
            name: name.text,
            name_span: name.span,
            array_dimensions: Vec::new(),
            span,
        }));
        Ok(())
    }

    fn parse_struct(&mut self) -> ParseResult<StructSyntax> {
        let start: Token = self.expect("struct")?;
        let name: Token = self.expect_identifier()?;
        self.type_names.insert(name.text.clone());
        self.expect("{")?;
        let mut fields: Vec<VariableDeclarationSyntax> = Vec::new();
        while !self.accept("}") {
            if self.accept(";") {
                continue;
            }
            let field_start: usize = self.position;
            self.skip_modifiers();
            self.parse_type()?;
            let is_method: bool = self.current().kind == TokenKind::Identifier && self.peek(1).is("(");
            self.position = field_start;
            if is_method {
                return Err(self.error_at("Struct member functions are not supported yet", self.current().span));
            }
            fields.push(self.parse_variable_declaration(StorageModifiers::NONE)?);
        }
        self.accept(";");
        let span: SourceSpan = self.span_from(start.span);
        Ok(StructSyntax { name: name.text, name_span: name.span, fields, span })
    }

    fn parse_function(&mut self, return_type: TypeSyntax, start: SourceSpan) -> ParseResult<FunctionSyntax> {
        let name: Token = self.expect_identifier()?;
        self.expect("(")?;
        let mut parameters: Vec<ParameterSyntax> = Vec::new();
        if self.is("void") && self.peek(1).is(")") {
            self.advance();
        }
        if !self.accept(")") {
            loop {
                parameters.push(self.parse_parameter()?);
                if !self.accept(",") {
                    break;
                }
            }
            self.expect(")")?;
        }
        self.skip_semantic();
        // C++ trailing specifiers
        while self.current().kind == TokenKind::Identifier
            && matches!(self.current().text.as_str(), "noexcept" | "const")
        {
            self.advance();
        }
        let body: Option<BlockSyntax> = if self.accept(";") { None } else { Some(self.parse_block()?) };
        let span: SourceSpan = self.span_from(start);
        Ok(FunctionSyntax { return_type, name: name.text, name_span: name.span, parameters, body, span })
    }

    fn parse_parameter(&mut self) -> ParseResult<ParameterSyntax> {
        let start: SourceSpan = self.current().span;
        let mut mode: ParameterMode = ParameterMode::In;
        let mut is_const: bool = false;
        while self.current().kind == TokenKind::Identifier {
            let text: String = self.current().text.clone();
            if matches!(text.as_str(), "in" | "out" | "inout") {
                self.advance();
                mode = match text.as_str() {
                    "out" => ParameterMode::Out,
                    "inout" => ParameterMode::InOut,
                    _ => mode,
                };
            } else if text == "const" {
                is_const = true;
                self.advance();
            } else if text == "uniform" || is_skipped_modifier(&text) {
                self.advance();
            } else {
                break;
            }
        }
        let ty: TypeSyntax = self.parse_type()?;
        is_const |= self.accept("const");
        // C++ references: const T& reads, T& writes back
        if self.accept("&") {
            mode = if is_const { ParameterMode::In } else { ParameterMode::InOut };
        }
        let name: Token = self.expect_identifier()?;
        let dimensions: Vec<Option<ExpressionSyntax>> = self.parse_array_dimensions()?;
        self.skip_semantic();
        let default_value: Option<ExpressionSyntax> =
            if self.accept("=") { Some(self.parse_assignment()?) } else { None };
        let span: SourceSpan = self.span_from(start);
        Ok(ParameterSyntax {
            mode,
            is_const,
            ty,
            name: name.text,
            name_span: name.span,
            array_dimensions: dimensions,
            default_value,
            span,
        })
    }

    fn parse_variable_declaration(
        &mut self,
        extra_modifiers: StorageModifiers,
    ) -> ParseResult<VariableDeclarationSyntax> {
        let start: SourceSpan = self.current().span;
        let modifiers: StorageModifiers = self.skip_modifiers() | extra_modifiers;
        let ty: TypeSyntax = self.parse_type()?;
        self.parse_declarators(modifiers, ty, start)
    }

    fn parse_declarators(
        &mut self,
        modifiers: StorageModifiers,
        ty: TypeSyntax,
        start: SourceSpan,
    ) -> ParseResult<VariableDeclarationSyntax> {
        let mut declarators: Vec<DeclaratorSyntax> = Vec::new();
        loop {
            let name: Token = self.expect_identifier()?;
            let dimensions: Vec<Option<ExpressionSyntax>> = self.parse_array_dimensions()?;
            self.skip_semantic();
            let mut initializer: Option<ExpressionSyntax> = None;
            if self.accept("=") {
                initializer =
                    Some(if self.is("{") { self.parse_initializer_list()? } else { self.parse_assignment()? });
            } else if self.is("{") {
                // C++ brace initialisation: float3 v{1, 2, 3}
                initializer = Some(self.parse_initializer_list()?);
            }
            declarators.push(DeclaratorSyntax {
                name: name.text,
                name_span: name.span,
                array_dimensions: dimensions,
                initializer,
            });
            if !self.accept(",") {
                break;
            }
        }
        self.expect_statement_end()?;
        let span: SourceSpan = self.span_from(start);
        Ok(VariableDeclarationSyntax { modifiers, ty, declarators, span })
    }

    fn parse_array_dimensions(&mut self) -> ParseResult<Vec<Option<ExpressionSyntax>>> {
        let mut dimensions: Vec<Option<ExpressionSyntax>> = Vec::new();
        while self.accept("[") {
            dimensions.push(if self.is("]") { None } else { Some(self.parse_expression()?) });
            self.expect("]")?;
        }
        Ok(dimensions)
    }

    fn skip_modifiers(&mut self) -> StorageModifiers {
        let mut modifiers: StorageModifiers = StorageModifiers::NONE;
        while self.current().kind == TokenKind::Identifier {
            match self.current().text.as_str() {
                "const" => modifiers = modifiers | StorageModifiers::CONST,
                "static" => modifiers = modifiers | StorageModifiers::STATIC,
                "groupshared" => modifiers = modifiers | StorageModifiers::GROUP_SHARED,
                "uniform" => modifiers = modifiers | StorageModifiers::UNIFORM,
                other => {
                    if !is_skipped_modifier(other) {
                        return modifiers;
                    }
                }
            }
            self.advance();
        }
        modifiers
    }

    fn skip_attributes(&mut self) {
        while self.is("[") {
            let mut depth: i32 = 0;
            loop {
                depth += match self.current().text.as_str() {
                    "[" => 1,
                    "]" => -1,
                    _ => 0,
                };
                self.advance();
                if !(depth > 0 && !self.is_end()) {
                    break;
                }
            }
        }
    }

    /// `: SV_Target`, `: register(t0)`, `: packoffset(c0)` are irrelevant here.
    fn skip_semantic(&mut self) {
        while self.is(":") && self.peek(1).kind == TokenKind::Identifier {
            self.advance();
            self.advance();
            if self.accept("(") {
                while !self.is_end() && !self.accept(")") {
                    self.advance();
                }
            }
        }
    }

    fn skip_register(&mut self) {
        self.skip_semantic();
    }

    // ---- Types ----

    fn is_type_start(&self, offset: usize) -> bool {
        let token: &Token = self.peek(offset);
        if token.kind != TokenKind::Identifier {
            return false;
        }
        if token.text == "std" && self.peek(offset + 1).is("::") {
            return self.is_type_start(offset + 2);
        }
        matches!(token.text.as_str(), "auto" | "unsigned" | "signed" | "long" | "vector" | "matrix" | "short")
            || is_builtin_type_name(&token.text)
            || self.type_names.contains(&token.text)
    }

    /// Skips a type at the cursor (lookahead only). Returns the offset after it.
    fn scan_type(&self, mut offset: usize) -> Option<usize> {
        if !self.is_type_start(offset) {
            return None;
        }
        if self.peek(offset).text == "std" {
            offset += 2;
        }
        let text: String = self.peek(offset).text.clone();
        if is_integer_spelling(&text) {
            while self.peek(offset).kind == TokenKind::Identifier
                && (is_integer_spelling(&self.peek(offset).text)
                    || matches!(self.peek(offset).text.as_str(), "int" | "char"))
            {
                offset += 1;
            }
            return Some(offset);
        }
        offset += 1;
        if matches!(text.as_str(), "vector" | "matrix") && self.peek(offset).is("<") {
            let mut depth: i32 = 0;
            loop {
                depth += if self.peek(offset).is("<") {
                    1
                } else if self.peek(offset).is(">") {
                    -1
                } else if self.peek(offset).is(">>") {
                    -2
                } else {
                    0
                };
                offset += 1;
                if !(depth > 0 && self.peek(offset).kind != TokenKind::End) {
                    break;
                }
            }
        }
        Some(offset)
    }

    fn parse_type(&mut self) -> ParseResult<TypeSyntax> {
        let start: SourceSpan = self.current().span;
        if self.is("std") && self.peek(1).is("::") {
            self.advance();
            self.advance();
        }
        if self.accept("auto") {
            return Ok(TypeSyntax::Auto { span: start });
        }
        // C++ integer spellings
        if self.current().kind == TokenKind::Identifier && is_integer_spelling(&self.current().text) {
            let mut is_unsigned: bool = false;
            let mut longs: i32 = 0;
            while self.current().kind == TokenKind::Identifier
                && (is_integer_spelling(&self.current().text) || matches!(self.current().text.as_str(), "int" | "char"))
            {
                let word: String = self.advance().text;
                is_unsigned |= word == "unsigned";
                longs += if word == "long" { 1 } else { 0 };
            }
            let name: &str = if longs >= 2 {
                if is_unsigned { "uint64_t" } else { "int64_t" }
            } else if is_unsigned {
                "uint"
            } else {
                "int"
            };
            return Ok(TypeSyntax::Named { name: name.to_string(), span: self.span_from(start) });
        }
        let token: Token = self.expect_identifier()?;
        if matches!(token.text.as_str(), "vector" | "matrix") && self.accept("<") {
            let element: TypeSyntax = self.parse_type()?;
            let mut dimensions: Vec<ExpressionSyntax> = Vec::new();
            while self.accept(",") {
                dimensions.push(self.parse_binary(BINARY_LEVELS.len() - 2)?);
            }
            self.expect_closing_angle()?;
            return Ok(TypeSyntax::Generic {
                name: token.text,
                element: Box::new(element),
                dimensions,
                span: self.span_from(start),
            });
        }
        if !is_builtin_type_name(&token.text)
            && !self.type_names.contains(&token.text)
            && !matches!(token.text.as_str(), "vector" | "matrix")
        {
            return Err(self.error_at(format!("Unknown type '{}'", token.text), token.span));
        }
        Ok(TypeSyntax::Named { name: token.text, span: token.span })
    }

    /// `>` closing a template argument list; splits a `>>`.
    fn expect_closing_angle(&mut self) -> ParseResult<()> {
        if self.is(">>") {
            let both: &mut Token = &mut self.tokens[self.position];
            both.text = ">".to_string();
            both.span =
                SourceSpan { offset: both.span.offset + 1, length: 1, column: both.span.column + 1, ..both.span };
            return Ok(());
        }
        self.expect(">")?;
        Ok(())
    }

    // ---- Statements ----

    fn is_statement_keyword(&self) -> bool {
        self.current().kind == TokenKind::Identifier
            && matches!(
                self.current().text.as_str(),
                "if" | "for" | "while" | "do" | "switch" | "return" | "break" | "continue" | "discard"
            )
    }

    /// A declaration starts with modifiers and a type followed by a name.
    fn is_declaration_start(&self) -> bool {
        let mut offset: usize = 0;
        while self.peek(offset).kind == TokenKind::Identifier
            && (matches!(self.peek(offset).text.as_str(), "const" | "static" | "groupshared" | "uniform")
                || is_skipped_modifier(&self.peek(offset).text))
        {
            offset += 1;
        }
        let Some(mut after_type) = self.scan_type(offset) else {
            return false;
        };
        while self.peek(after_type).is("const") {
            after_type += 1;
        }
        self.peek(after_type).kind == TokenKind::Identifier
    }

    /// A function definition in a calculator line: type name '('.
    fn is_function_start(&self) -> bool {
        let mut offset: usize = 0;
        while self.peek(offset).kind == TokenKind::Identifier
            && (matches!(self.peek(offset).text.as_str(), "static" | "const")
                || is_skipped_modifier(&self.peek(offset).text))
        {
            offset += 1;
        }
        let after_type: Option<usize> =
            if self.peek(offset).is("void") { Some(offset + 1) } else { self.scan_type(offset) };
        match after_type {
            Some(after_type) => {
                self.peek(after_type).kind == TokenKind::Identifier && self.peek(after_type + 1).is("(")
            }
            None => false,
        }
    }

    fn parse_block(&mut self) -> ParseResult<BlockSyntax> {
        let start: Token = self.expect("{")?;
        self.block_depth += 1;
        let result: ParseResult<BlockSyntax> = self.parse_block_body(start.span);
        self.block_depth -= 1;
        result
    }

    fn parse_block_body(&mut self, start: SourceSpan) -> ParseResult<BlockSyntax> {
        let mut statements: Vec<StatementSyntax> = Vec::new();
        while !self.is("}") && !self.is_end() {
            let statement_start: usize = self.position;
            match self.parse_statement() {
                Ok(statement) => statements.push(statement),
                Err(error) => {
                    self.report(error);
                    self.recover(statement_start);
                }
            }
        }
        self.expect("}")?;
        Ok(BlockSyntax { statements, span: self.span_from(start) })
    }

    fn parse_statement(&mut self) -> ParseResult<StatementSyntax> {
        self.skip_attributes();
        let start: Token = self.current().clone();
        if self.is("{") {
            let block: BlockSyntax = self.parse_block()?;
            let span: SourceSpan = block.span;
            return Ok(StatementSyntax::new(StatementKind::Block(block), span));
        }
        if self.accept(";") {
            return Ok(StatementSyntax::new(StatementKind::Empty, start.span));
        }
        if self.accept("if") {
            let condition: ExpressionSyntax = self.parse_parenthesized()?;
            let then: StatementSyntax = self.parse_statement()?;
            let otherwise: Option<Box<StatementSyntax>> =
                if self.accept("else") { Some(Box::new(self.parse_statement()?)) } else { None };
            let span: SourceSpan = self.span_from(start.span);
            return Ok(StatementSyntax::new(StatementKind::If { condition, then: Box::new(then), otherwise }, span));
        }
        if self.accept("for") {
            self.expect("(")?;
            self.nesting += 1;
            let initializer: Option<Box<StatementSyntax>> =
                if self.accept(";") { None } else { Some(Box::new(self.parse_simple_statement()?)) };
            let condition: Option<ExpressionSyntax> = if self.is(";") { None } else { Some(self.parse_expression()?) };
            self.expect(";")?;
            let step: Option<ExpressionSyntax> = if self.is(")") { None } else { Some(self.parse_expression()?) };
            self.nesting -= 1;
            self.expect(")")?;
            let body: StatementSyntax = self.parse_statement()?;
            let span: SourceSpan = self.span_from(start.span);
            return Ok(StatementSyntax::new(
                StatementKind::For { initializer, condition, step, body: Box::new(body) },
                span,
            ));
        }
        if self.accept("while") {
            let condition: ExpressionSyntax = self.parse_parenthesized()?;
            let body: StatementSyntax = self.parse_statement()?;
            let span: SourceSpan = self.span_from(start.span);
            return Ok(StatementSyntax::new(
                StatementKind::While { condition, body: Box::new(body), is_do_while: false },
                span,
            ));
        }
        if self.accept("do") {
            let body: StatementSyntax = self.parse_statement()?;
            self.expect("while")?;
            let condition: ExpressionSyntax = self.parse_parenthesized()?;
            self.expect_statement_end()?;
            let span: SourceSpan = self.span_from(start.span);
            return Ok(StatementSyntax::new(
                StatementKind::While { condition, body: Box::new(body), is_do_while: true },
                span,
            ));
        }
        if self.is("switch") {
            return self.parse_switch();
        }
        if self.accept("return") {
            let value: Option<ExpressionSyntax> =
                if self.is(";") || self.at_line_break() { None } else { Some(self.parse_expression()?) };
            self.expect_statement_end()?;
            let span: SourceSpan = self.span_from(start.span);
            return Ok(StatementSyntax::new(StatementKind::Return(value), span));
        }
        if self.accept("break") || self.accept("continue") || self.accept("discard") {
            self.expect_statement_end()?;
            return Ok(StatementSyntax::new(StatementKind::Jump(start.text), start.span));
        }
        self.parse_simple_statement()
    }

    /// A declaration or an expression, ending with ';'.
    fn parse_simple_statement(&mut self) -> ParseResult<StatementSyntax> {
        let start: SourceSpan = self.current().span;
        if self.is_declaration_start() {
            let declaration: VariableDeclarationSyntax = self.parse_variable_declaration(StorageModifiers::NONE)?;
            let span: SourceSpan = declaration.span;
            return Ok(StatementSyntax::new(StatementKind::VariableDeclaration(declaration), span));
        }
        let expression: ExpressionSyntax = self.parse_expression()?;
        self.expect_statement_end()?;
        let span: SourceSpan = self.span_from(start);
        Ok(StatementSyntax::new(StatementKind::Expression(expression), span))
    }

    /// ';', optional at the very end of a calculator line and at the end of a worksheet line.
    fn expect_statement_end(&mut self) -> ParseResult<()> {
        if !(self.is_interactive && (self.is_end() || self.at_line_break())) {
            self.expect(";")?;
        }
        Ok(())
    }

    fn parse_switch(&mut self) -> ParseResult<StatementSyntax> {
        let start: Token = self.expect("switch")?;
        let value: ExpressionSyntax = self.parse_parenthesized()?;
        self.expect("{")?;
        self.block_depth += 1;
        let result: ParseResult<StatementSyntax> = self.parse_switch_body(start.span, value);
        self.block_depth -= 1;
        result
    }

    fn parse_switch_body(&mut self, start: SourceSpan, value: ExpressionSyntax) -> ParseResult<StatementSyntax> {
        let mut sections: Vec<SwitchSectionSyntax> = Vec::new();
        while !self.accept("}") {
            let section_start: SourceSpan = self.current().span;
            let mut labels: Vec<Option<ExpressionSyntax>> = Vec::new();
            while self.is("case") || self.is("default") {
                if self.accept("default") {
                    labels.push(None);
                } else {
                    self.advance();
                    labels.push(Some(self.parse_conditional()?));
                }
                self.expect(":")?;
            }
            if labels.is_empty() {
                return Err(self.error_at("Expected 'case' or 'default'", self.current().span));
            }
            let mut statements: Vec<StatementSyntax> = Vec::new();
            while !self.is("case") && !self.is("default") && !self.is("}") && !self.is_end() {
                statements.push(self.parse_statement()?);
            }
            sections.push(SwitchSectionSyntax { labels, statements, span: self.span_from(section_start) });
        }
        let span: SourceSpan = self.span_from(start);
        Ok(StatementSyntax::new(StatementKind::Switch { value, sections }, span))
    }

    // ---- Expressions (C precedence) ----

    fn parse_expression(&mut self) -> ParseResult<ExpressionSyntax> {
        let start: SourceSpan = self.current().span;
        let mut expression: ExpressionSyntax = self.parse_assignment()?;
        while !self.at_line_break() && self.is(",") {
            self.advance();
            let right: ExpressionSyntax = self.parse_assignment()?;
            let span: SourceSpan = self.span_from(start);
            expression = ExpressionSyntax::new(
                ExpressionKind::Binary {
                    operator: ",".to_string(),
                    left: Box::new(expression),
                    right: Box::new(right),
                },
                span,
            );
        }
        Ok(expression)
    }

    fn parse_assignment(&mut self) -> ParseResult<ExpressionSyntax> {
        let start: SourceSpan = self.current().span;
        let target: ExpressionSyntax = self.parse_conditional()?;
        if !self.at_line_break()
            && self.current().kind == TokenKind::Punctuator
            && ASSIGNMENT_OPERATORS.contains(&self.current().text.as_str())
        {
            let operation: String = self.advance().text;
            let value: ExpressionSyntax =
                if self.is("{") { self.parse_initializer_list()? } else { self.parse_assignment()? };
            let span: SourceSpan = self.span_from(start);
            return Ok(ExpressionSyntax::new(
                ExpressionKind::Assignment { operator: operation, target: Box::new(target), value: Box::new(value) },
                span,
            ));
        }
        Ok(target)
    }

    fn parse_conditional(&mut self) -> ParseResult<ExpressionSyntax> {
        let start: SourceSpan = self.current().span;
        let condition: ExpressionSyntax = self.parse_binary(0)?;
        if !self.at_line_break() && self.accept("?") {
            let when_true: ExpressionSyntax = self.parse_expression()?;
            self.expect(":")?;
            let when_false: ExpressionSyntax = self.parse_assignment()?;
            let span: SourceSpan = self.span_from(start);
            return Ok(ExpressionSyntax::new(
                ExpressionKind::Conditional {
                    condition: Box::new(condition),
                    when_true: Box::new(when_true),
                    when_false: Box::new(when_false),
                },
                span,
            ));
        }
        Ok(condition)
    }

    fn parse_binary(&mut self, level: usize) -> ParseResult<ExpressionSyntax> {
        if level == BINARY_LEVELS.len() {
            return self.parse_unary();
        }
        let start: SourceSpan = self.current().span;
        let mut left: ExpressionSyntax = self.parse_binary(level + 1)?;
        while !self.at_line_break()
            && self.current().kind == TokenKind::Punctuator
            && BINARY_LEVELS[level].contains(&self.current().text.as_str())
        {
            let operation: String = self.advance().text;
            let right: ExpressionSyntax = self.parse_binary(level + 1)?;
            let span: SourceSpan = self.span_from(start);
            left = ExpressionSyntax::new(
                ExpressionKind::Binary { operator: operation, left: Box::new(left), right: Box::new(right) },
                span,
            );
        }
        Ok(left)
    }

    fn parse_unary(&mut self) -> ParseResult<ExpressionSyntax> {
        let start: Token = self.current().clone();
        if start.kind == TokenKind::Punctuator && matches!(start.text.as_str(), "-" | "+" | "!" | "~") {
            self.advance();
            let operand: ExpressionSyntax = self.parse_unary()?;
            let span: SourceSpan = self.span_from(start.span);
            return Ok(ExpressionSyntax::new(
                ExpressionKind::Unary { operator: start.text, operand: Box::new(operand) },
                span,
            ));
        }
        if start.kind == TokenKind::Punctuator && matches!(start.text.as_str(), "++" | "--") {
            self.advance();
            let target: ExpressionSyntax = self.parse_unary()?;
            let span: SourceSpan = self.span_from(start.span);
            return Ok(ExpressionSyntax::new(
                ExpressionKind::Increment { operator: start.text, is_prefix: true, target: Box::new(target) },
                span,
            ));
        }
        // C cast: (float3)x, (float[2])x
        if self.is("(")
            && let Some(after_type) = self.scan_type(1)
            && after_type > 0
        {
            let mut close: usize = after_type;
            while self.peek(close).is("[") {
                while !self.peek(close).is("]") && self.peek(close).kind != TokenKind::End {
                    close += 1;
                }
                close += 1;
            }
            if self.peek(close).is(")") && !self.peek(close + 1).is(".") {
                self.advance();
                let ty: TypeSyntax = self.parse_type()?;
                let dimensions: Vec<Option<ExpressionSyntax>> = self.parse_array_dimensions()?;
                self.expect(")")?;
                let operand: ExpressionSyntax = self.parse_unary()?;
                let mut lengths: Vec<ExpressionSyntax> = Vec::new();
                for dimension in dimensions {
                    match dimension {
                        Some(length) => lengths.push(length),
                        None => return Err(self.error_at("A cast needs array lengths", start.span)),
                    }
                }
                let span: SourceSpan = self.span_from(start.span);
                return Ok(ExpressionSyntax::new(
                    ExpressionKind::Cast { ty, array_dimensions: lengths, operand: Box::new(operand) },
                    span,
                ));
            }
        }
        let primary: ExpressionSyntax = self.parse_primary()?;
        self.parse_postfix(primary)
    }

    fn parse_postfix(&mut self, mut expression: ExpressionSyntax) -> ParseResult<ExpressionSyntax> {
        while !self.at_line_break() {
            let token: Token = self.current().clone();
            if self.accept(".") {
                let member: Token = self.expect_identifier()?;
                let span: SourceSpan = expression.span.to(member.span);
                expression = ExpressionSyntax::new(
                    ExpressionKind::Member {
                        target: Box::new(expression),
                        member: member.text,
                        member_span: member.span,
                    },
                    span,
                );
            } else if self.accept("[") {
                self.nesting += 1;
                let index: ExpressionSyntax = self.parse_expression()?;
                self.nesting -= 1;
                let close: Token = self.expect("]")?;
                let span: SourceSpan = expression.span.to(close.span);
                expression = ExpressionSyntax::new(
                    ExpressionKind::Index { target: Box::new(expression), index: Box::new(index) },
                    span,
                );
            } else if token.kind == TokenKind::Punctuator && matches!(token.text.as_str(), "++" | "--") {
                self.advance();
                let span: SourceSpan = expression.span.to(token.span);
                expression = ExpressionSyntax::new(
                    ExpressionKind::Increment { operator: token.text, is_prefix: false, target: Box::new(expression) },
                    span,
                );
            } else {
                return Ok(expression);
            }
        }
        Ok(expression)
    }

    fn parse_primary(&mut self) -> ParseResult<ExpressionSyntax> {
        let token: Token = self.current().clone();
        if token.kind == TokenKind::Number {
            self.advance();
            let number: ExpressionSyntax = self.parse_number(&token)?;
            if let Some(is_unit_name) = self.is_unit_name
                && !self.at_line_break()
                && self.current().kind == TokenKind::Identifier
                && is_unit_name(&self.current().text)
                && !self.peek(1).is("(")
            {
                return self.parse_unit_literal(number, token.span);
            }
            return Ok(number);
        }
        if self.is("(") {
            return self.parse_parenthesized();
        }
        if self.is("{") {
            return self.parse_initializer_list();
        }
        if token.kind != TokenKind::Identifier {
            let message: String = if token.kind == TokenKind::End {
                "Unexpected end of input".to_string()
            } else {
                format!("Unexpected '{}'", token.text)
            };
            return Err(self.error_at(message, token.span));
        }

        if matches!(token.text.as_str(), "true" | "false") {
            self.advance();
            let bits: u64 = if token.text == "true" { 1 } else { 0 };
            return Ok(ExpressionSyntax::new(
                ExpressionKind::Literal { kind: ScalarKind::Bool, bits, text: token.text },
                token.span,
            ));
        }
        if matches!(token.text.as_str(), "static_cast" | "reinterpret_cast") {
            self.advance();
            self.expect("<")?;
            let ty: TypeSyntax = self.parse_type()?;
            self.expect_closing_angle()?;
            self.expect("(")?;
            let operand: ExpressionSyntax = self.parse_expression()?;
            self.expect(")")?;
            let span: SourceSpan = self.span_from(token.span);
            return Ok(ExpressionSyntax::new(
                ExpressionKind::Cast { ty, array_dimensions: Vec::new(), operand: Box::new(operand) },
                span,
            ));
        }
        // vector<float, 3>(...) and the C++ spellings unsigned(x), std::uint32_t(x)
        if self.is_type_start(0)
            && matches!(token.text.as_str(), "vector" | "matrix" | "unsigned" | "long" | "std")
            && !(token.text == "std" && !self.is_type_start(2))
        {
            let ty: TypeSyntax = self.parse_type()?;
            self.expect("(")?;
            let arguments: Vec<ExpressionSyntax> = self.parse_arguments()?;
            let span: SourceSpan = self.span_from(token.span);
            return Ok(ExpressionSyntax::new(ExpressionKind::Constructor { ty, arguments }, span));
        }

        self.advance();
        let mut name: String = token.text.clone();
        // std::sqrt, std::clamp... are the HLSL intrinsics of the same name
        if name == "std" && self.accept("::") {
            name = self.expect_identifier()?.text;
        }
        if !self.at_line_break() && self.accept("(") {
            let arguments: Vec<ExpressionSyntax> = self.parse_arguments()?;
            let span: SourceSpan = self.span_from(token.span);
            return Ok(ExpressionSyntax::new(ExpressionKind::Call { name, name_span: token.span, arguments }, span));
        }
        Ok(ExpressionSyntax::new(ExpressionKind::Name(name), token.span))
    }

    /// Arguments after '(' up to and including ')'.
    fn parse_arguments(&mut self) -> ParseResult<Vec<ExpressionSyntax>> {
        self.nesting += 1;
        let result: ParseResult<Vec<ExpressionSyntax>> = self.parse_argument_list();
        self.nesting -= 1;
        result
    }

    /// `( expression )`, the line-break rule suspended inside.
    fn parse_parenthesized(&mut self) -> ParseResult<ExpressionSyntax> {
        self.expect("(")?;
        self.nesting += 1;
        let inner: ExpressionSyntax = self.parse_expression()?;
        self.nesting -= 1;
        self.expect(")")?;
        Ok(inner)
    }

    fn parse_argument_list(&mut self) -> ParseResult<Vec<ExpressionSyntax>> {
        let mut arguments: Vec<ExpressionSyntax> = Vec::new();
        if self.accept(")") {
            return Ok(arguments);
        }
        loop {
            arguments.push(self.parse_assignment()?);
            if !self.accept(",") {
                break;
            }
        }
        self.expect(")")?;
        Ok(arguments)
    }

    fn parse_initializer_list(&mut self) -> ParseResult<ExpressionSyntax> {
        let start: Token = self.expect("{")?;
        self.nesting += 1;
        let result: ParseResult<ExpressionSyntax> = self.parse_initializer_elements(start.span);
        self.nesting -= 1;
        result
    }

    fn parse_initializer_elements(&mut self, start: SourceSpan) -> ParseResult<ExpressionSyntax> {
        let mut elements: Vec<ExpressionSyntax> = Vec::new();
        while !self.is("}") {
            elements.push(if self.is("{") { self.parse_initializer_list()? } else { self.parse_assignment()? });
            if !self.accept(",") {
                break;
            }
        }
        self.expect("}")?;
        Ok(ExpressionSyntax::new(ExpressionKind::InitializerList(elements), self.span_from(start)))
    }

    /// `3 km/h`, `9.81 m/s^2`, `2 m^-1`: continues only into another unit, so `6 m / x` divides.
    fn parse_unit_literal(&mut self, number: ExpressionSyntax, start: SourceSpan) -> ParseResult<ExpressionSyntax> {
        let is_unit_name: &dyn Fn(&str) -> bool = self.is_unit_name.expect("unit literals need a unit table");
        let mut scale: f64 = 1.0;
        let mut dimension: Dimension = Dimension::NONE;
        let mut text: Vec<String> = Vec::new();
        let mut is_division: bool = false;
        loop {
            let unit_token: Token = self.expect_identifier()?;
            let unit: &UnitDefinition = &UNITS[unit_token.text.as_str()];
            let mut exponent: i32 = 1;
            let mut factor_text: String = unit.name.to_string();
            if !self.at_line_break()
                && self.is("^")
                && (self.peek(1).kind == TokenKind::Number
                    || (self.peek(1).is("-") && self.peek(2).kind == TokenKind::Number))
            {
                self.advance();
                let is_negative: bool = self.accept("-");
                let exponent_token: Token = self.advance();
                let magnitude: i32 = exponent_token.text.parse().map_err(|_| {
                    self.error_at(format!("Invalid unit exponent '{}'", exponent_token.text), exponent_token.span)
                })?;
                exponent = magnitude * if is_negative { -1 } else { 1 };
                factor_text.push_str(&format!("^{exponent}"));
            }
            let factor_scale: f64 = unit.to_si.powf(exponent as f64);
            let factor_dimension: Dimension =
                unit.dimension.power(exponent as f64).expect("integer powers always exist");
            scale = if is_division { scale / factor_scale } else { scale * factor_scale };
            dimension =
                if is_division { dimension.divide(&factor_dimension) } else { dimension.multiply(&factor_dimension) };
            let separator: &str = if text.is_empty() {
                ""
            } else if is_division {
                "/"
            } else {
                "*"
            };
            text.push(format!("{separator}{factor_text}"));

            if !self.at_line_break()
                && matches!(self.current().text.as_str(), "/" | "*")
                && self.peek(1).kind == TokenKind::Identifier
                && is_unit_name(&self.peek(1).text)
                && !self.peek(2).is("(")
            {
                is_division = self.advance().text == "/";
                continue;
            }
            break;
        }
        let ExpressionKind::Literal { kind, bits, .. } = number.kind else {
            unreachable!("unit literals start with a number literal");
        };
        let number_value: f64 = scalars::to_number(kind, bits);
        let span: SourceSpan = self.span_from(start);
        Ok(ExpressionSyntax::new(
            ExpressionKind::UnitLiteral { si_value: number_value * scale, dimension, unit_text: text.concat() },
            span,
        ))
    }

    /// HLSL literals: unsuffixed `1` / `1.5` are literal int / literal float (64-bit), `u` uint, `l`/`ll` int64, `ul`
    /// uint64, `f` float, `h` half (= float without 16-bit types), `l`/`lf` on a float double. Leading 0 is octal.
    fn parse_number(&self, token: &Token) -> ParseResult<ExpressionSyntax> {
        let invalid = || self.error_at(format!("Invalid number '{}'", token.text), token.span);
        let text: String = token.text.to_lowercase();
        let is_hex: bool = text.starts_with("0x");
        let digits: &str =
            if is_hex { text[2..].trim_end_matches(['u', 'l']) } else { text.trim_end_matches(['u', 'l', 'f', 'h']) };
        let suffix: &str = &text[(if is_hex { 2 + digits.len() } else { digits.len() })..];
        let is_float: bool =
            !is_hex && (digits.contains('.') || digits.contains('e') || matches!(suffix, "f" | "h" | "lf"));
        if is_float {
            let value: f64 = digits.parse::<f64>().map_err(|_| invalid())?;
            let kind: ScalarKind = match suffix {
                "" => ScalarKind::LiteralFloat,
                "l" | "lf" => ScalarKind::Double,
                _ => ScalarKind::Float,
            };
            let bits: u64 =
                if kind == ScalarKind::Float { scalars::from_float(value as f32) } else { scalars::from_double(value) };
            return Ok(ExpressionSyntax::new(
                ExpressionKind::Literal { kind, bits, text: token.text.clone() },
                token.span,
            ));
        }

        let integer: u64 = if is_hex {
            u64::from_str_radix(digits, 16)
        } else if digits.len() > 1 && digits.starts_with('0') {
            u64::from_str_radix(digits, 8)
        } else {
            digits.parse::<u64>()
        }
        .map_err(|_| invalid())?;
        let integer_kind: ScalarKind = match suffix {
            "" => ScalarKind::LiteralInt,
            "u" if integer > u32::MAX as u64 => ScalarKind::UInt64,
            "u" => ScalarKind::UInt,
            "l" | "ll" => ScalarKind::Int64,
            _ => ScalarKind::UInt64,
        };
        let encoded: u64 = if integer_kind == ScalarKind::UInt { integer as u32 as u64 } else { integer };
        Ok(ExpressionSyntax::new(
            ExpressionKind::Literal { kind: integer_kind, bits: encoded, text: token.text.clone() },
            token.span,
        ))
    }

    // ---- Token helpers ----

    fn is_end(&self) -> bool {
        self.current().kind == TokenKind::End
    }

    fn current(&self) -> &Token {
        &self.tokens[self.position]
    }

    fn previous(&self) -> &Token {
        &self.tokens[self.position.saturating_sub(1)]
    }

    fn peek(&self, offset: usize) -> &Token {
        &self.tokens[(self.position + offset).min(self.tokens.len() - 1)]
    }

    fn advance(&mut self) -> Token {
        let token: Token = self.tokens[self.position].clone();
        if self.position < self.tokens.len() - 1 {
            self.position += 1;
        }
        token
    }

    fn is(&self, text: &str) -> bool {
        let current: &Token = self.current();
        current.kind != TokenKind::End && current.kind != TokenKind::String && current.text == text
    }

    fn accept(&mut self, text: &str) -> bool {
        if self.is(text) {
            self.advance();
            return true;
        }
        false
    }

    fn expect(&mut self, text: &str) -> ParseResult<Token> {
        if self.is(text) {
            return Ok(self.advance());
        }
        let message: String = if self.is_end() {
            format!("Expected '{text}' before the end")
        } else {
            format!("Expected '{}' but found '{}'", text, self.current().text)
        };
        Err(self.error_at(message, self.current().span))
    }

    fn expect_identifier(&mut self) -> ParseResult<Token> {
        if self.current().kind == TokenKind::Identifier {
            return Ok(self.advance());
        }
        let message: String = if self.is_end() {
            "Expected a name before the end".to_string()
        } else {
            format!("Expected a name but found '{}'", self.current().text)
        };
        Err(self.error_at(message, self.current().span))
    }

    fn span_from(&self, start: SourceSpan) -> SourceSpan {
        start.to(self.previous().span)
    }

    fn error_at(&self, message: impl Into<String>, span: SourceSpan) -> ParseError {
        ParseError { message: message.into(), span }
    }

    fn report(&mut self, error: ParseError) {
        self.diagnostics.add(Diagnostic::new(
            DiagnosticSeverity::Error,
            error.message,
            self.source_name.clone(),
            error.span,
        ));
    }

    /// After an error: skip to the end of the statement or block, always making progress.
    fn recover(&mut self, start: usize) {
        if self.position == start {
            self.advance();
        }
        let mut depth: i32 = 0;
        while !self.is_end() {
            if self.is("{") {
                depth += 1;
            } else if self.is("}") {
                if depth == 0 {
                    return;
                }
                depth -= 1;
                if depth == 0 {
                    self.advance();
                    self.accept(";");
                    return;
                }
            } else if self.is(";") && depth == 0 {
                self.advance();
                return;
            }
            self.advance();
        }
    }
}
