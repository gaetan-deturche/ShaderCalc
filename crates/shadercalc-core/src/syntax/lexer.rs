use std::collections::HashSet;
use std::sync::Arc;

use crate::diagnostics::{DiagnosticBag, SourceSpan};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum TokenKind {
    Identifier,
    Number,
    String,
    Punctuator,
    End,
}

/// A token. `starts_line` marks the first token of a source line (preprocessor directives); `hidden` holds the
/// macros that must not expand it again. Span offsets and columns count UTF-16 code units, like JavaScript strings.
#[derive(Clone, Debug)]
pub struct Token {
    pub kind: TokenKind,
    pub text: String,
    pub span: SourceSpan,
    pub starts_line: bool,
    pub has_leading_space: bool,
    pub hidden: Option<Arc<HashSet<String>>>,
}

impl Token {
    pub fn new(
        kind: TokenKind,
        text: impl Into<String>,
        span: SourceSpan,
        starts_line: bool,
        has_leading_space: bool,
    ) -> Token {
        Token { kind, text: text.into(), span, starts_line, has_leading_space, hidden: None }
    }

    pub fn is(&self, text: &str) -> bool {
        matches!(self.kind, TokenKind::Punctuator | TokenKind::Identifier) && self.text == text
    }
}

const PUNCTUATORS: [&str; 49] = [
    "<<=", ">>=", "...", "::", "->", "++", "--", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||", "+=", "-=", "*=",
    "/=", "%=", "&=", "|=", "^=", "##", "+", "-", "*", "/", "%", "&", "|", "^", "~", "!", "<", ">", "=", "?", ":", ";",
    ",", ".", "(", ")", "[", "]", "{", "}", "#",
];

/// Whole-word literal suffixes; anything else after a number is a separate word (a unit in calculator lines).
const INTEGER_SUFFIXES: [&str; 7] = ["u", "l", "ul", "lu", "ll", "ull", "llu"];

const FLOAT_SUFFIXES: [&str; 4] = ["f", "h", "l", "lf"];

fn is_letter(character: char) -> bool {
    character.is_alphabetic()
}

fn is_letter_or_digit(character: char) -> bool {
    character.is_alphabetic() || character.is_ascii_digit()
}

struct Scanner {
    chars: Vec<char>,
    /// UTF-16 offset of each char (plus one past the end).
    offsets: Vec<usize>,
}

impl Scanner {
    fn new(source: &str) -> Scanner {
        let chars: Vec<char> = source.chars().collect();
        let mut offsets: Vec<usize> = Vec::with_capacity(chars.len() + 1);
        let mut offset: usize = 0;
        for character in &chars {
            offsets.push(offset);
            offset += character.len_utf16();
        }
        offsets.push(offset);
        Scanner { chars, offsets }
    }

    fn len(&self) -> usize {
        self.chars.len()
    }

    fn peek(&self, index: usize) -> char {
        if index < self.chars.len() { self.chars[index] } else { '\0' }
    }

    fn text(&self, start: usize, end: usize) -> String {
        self.chars[start..end].iter().collect()
    }

    fn next_is_newline(&self, index: usize) -> usize {
        if self.peek(index) == '\r' && self.peek(index + 1) == '\n' {
            2
        } else if self.peek(index) == '\n' {
            1
        } else {
            0
        }
    }

    fn scan_number(&self, mut index: usize) -> usize {
        let is_hex: bool = self.chars[index] == '0' && matches!(self.peek(index + 1), 'x' | 'X');
        if is_hex {
            index += 2;
            while index < self.len() && self.chars[index].is_ascii_hexdigit() {
                index += 1;
            }
            return self.scan_suffix(index, &INTEGER_SUFFIXES);
        }

        let mut is_float: bool = false;
        while index < self.len() && (self.chars[index].is_ascii_digit() || self.chars[index] == '.') {
            is_float |= self.chars[index] == '.';
            index += 1;
        }
        // Exponent only when digits follow, so `2 em` stays a number and a word
        if index < self.len() && matches!(self.chars[index], 'e' | 'E') {
            let digits: usize = if matches!(self.peek(index + 1), '+' | '-') { index + 2 } else { index + 1 };
            if self.peek(digits).is_ascii_digit() {
                is_float = true;
                index = digits;
                while index < self.len() && self.chars[index].is_ascii_digit() {
                    index += 1;
                }
            }
        }
        // `1f` is a float in HLSL; `2km` is the number 2 then the unit km
        let mut after_suffix: usize = self.scan_suffix(index, &FLOAT_SUFFIXES);
        if after_suffix == index && !is_float {
            after_suffix = self.scan_suffix(index, &INTEGER_SUFFIXES);
        }
        after_suffix
    }

    fn scan_suffix(&self, index: usize, suffixes: &[&str]) -> usize {
        let mut word_end: usize = index;
        while word_end < self.len() && (is_letter_or_digit(self.chars[word_end]) || self.chars[word_end] == '_') {
            word_end += 1;
        }
        let word: String = self.text(index, word_end).to_lowercase();
        if word_end > index && suffixes.contains(&word.as_str()) { word_end } else { index }
    }

    fn punctuator_at(&self, index: usize) -> Option<&'static str> {
        PUNCTUATORS.iter().copied().find(|candidate| {
            candidate.chars().enumerate().all(|(position, character)| self.peek(index + position) == character)
                && index + candidate.chars().count() <= self.len()
        })
    }
}

pub fn tokenize(source: &str, source_name: &str, diagnostics: &mut DiagnosticBag) -> Vec<Token> {
    let scanner: Scanner = Scanner::new(source);
    let mut tokens: Vec<Token> = Vec::new();
    let mut index: usize = 0;
    let mut line: usize = 1;
    let mut line_start: usize = 0;
    let mut starts_line: bool = true;
    let mut has_leading_space: bool = false;
    let column_of =
        |index: usize, line_start: usize| -> usize { scanner.offsets[index] - scanner.offsets[line_start] + 1 };

    while index < scanner.len() {
        let character: char = scanner.chars[index];
        if character == '\n' {
            index += 1;
            line += 1;
            line_start = index;
            starts_line = true;
            has_leading_space = false;
            continue;
        }
        // Line continuation: the next line belongs to this one
        if character == '\\' && scanner.next_is_newline(index + 1) > 0 {
            index += 1 + scanner.next_is_newline(index + 1);
            line += 1;
            line_start = index;
            has_leading_space = true;
            continue;
        }
        if character.is_whitespace() {
            index += 1;
            has_leading_space = true;
            continue;
        }
        if character == '/' && scanner.peek(index + 1) == '/' {
            while index < scanner.len() && scanner.chars[index] != '\n' {
                index += 1;
            }
            has_leading_space = true;
            continue;
        }
        if character == '/' && scanner.peek(index + 1) == '*' {
            let comment_line: usize = line;
            let comment_column: usize = column_of(index, line_start);
            index += 2;
            while index < scanner.len() && !(scanner.chars[index] == '*' && scanner.peek(index + 1) == '/') {
                if scanner.chars[index] == '\n' {
                    line += 1;
                    line_start = index + 1;
                }
                index += 1;
            }
            if index >= scanner.len() {
                diagnostics.error(
                    "Unterminated /* comment",
                    source_name,
                    SourceSpan::new(scanner.offsets[index], 0, comment_line, comment_column),
                );
            }
            index = (index + 2).min(scanner.len());
            has_leading_space = true;
            continue;
        }

        let start: usize = index;
        let column: usize = column_of(index, line_start);
        let kind: TokenKind;
        if character.is_ascii_digit() || (character == '.' && scanner.peek(index + 1).is_ascii_digit()) {
            index = scanner.scan_number(index);
            kind = TokenKind::Number;
        } else if is_letter(character) || character == '_' {
            while index < scanner.len() && (is_letter_or_digit(scanner.chars[index]) || scanner.chars[index] == '_') {
                index += 1;
            }
            kind = TokenKind::Identifier;
        } else if character == '"' || character == '\'' {
            index += 1;
            while index < scanner.len() && scanner.chars[index] != character && scanner.chars[index] != '\n' {
                index += if scanner.chars[index] == '\\' { 2 } else { 1 };
            }
            index = (index + 1).min(scanner.len());
            kind = TokenKind::String;
        } else {
            match scanner.punctuator_at(index) {
                Some(punctuator) => {
                    index += punctuator.chars().count();
                    kind = TokenKind::Punctuator;
                }
                None => {
                    diagnostics.error(
                        format!("Unexpected character '{character}'"),
                        source_name,
                        SourceSpan::new(scanner.offsets[index], character.len_utf16(), line, column),
                    );
                    index += 1;
                    continue;
                }
            }
        }

        let span: SourceSpan =
            SourceSpan::new(scanner.offsets[start], scanner.offsets[index] - scanner.offsets[start], line, column);
        tokens.push(Token::new(kind, scanner.text(start, index), span, starts_line, has_leading_space));
        starts_line = false;
        has_leading_space = false;
    }

    let end_span: SourceSpan = SourceSpan::new(scanner.offsets[index], 0, line, column_of(index, line_start));
    tokens.push(Token::new(TokenKind::End, "", end_span, true, true));
    tokens
}
