import { HighlightStyle, StreamLanguage, StringStream } from "@codemirror/language";
import { clike } from "@codemirror/legacy-modes/mode/clike";
import { tags } from "@lezer/highlight";

export const KEYWORDS: string[] = [
  "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "return", "discard", "struct", "typedef",
  "cbuffer", "tbuffer", "namespace", "using", "static", "const", "inline", "constexpr", "in", "out", "inout", "uniform", "true", "false",
  "auto", "static_cast", "unsigned", "register", "groupshared", "precise", "vector", "matrix", "void",
];

export const COMMON_TYPES: string[] = [
  "bool", "int", "uint", "float", "double", "half", "int2", "int3", "int4", "uint2", "uint3", "uint4", "float2", "float3", "float4",
  "float2x2", "float3x3", "float4x4", "int64_t", "uint64_t", "min16float", "vector", "matrix",
];

const TYPE_PATTERN: RegExp =
  /^(bool|int|uint|dword|half|float|double|min16float|min10float|min16int|min12int|min16uint|int32_t|uint32_t|int64_t|uint64_t|float32_t|float64_t)([1-4](x[1-4])?)?$/;

function words(list: string[]): Record<string, boolean> {
  const set: Record<string, boolean> = {};
  for (const word of list) {
    set[word] = true;
  }
  return set;
}

/** HLSL colouring: keywords, types, every intrinsic of the interpreter, numbers, comments, preprocessor lines. */
export function hlslLanguage(intrinsics: string[]): StreamLanguage<unknown> {
  return StreamLanguage.define(
    clike({
      name: "hlsl",
      keywords: words(KEYWORDS.filter((keyword) => keyword !== "true" && keyword !== "false")),
      types: (word: string) => TYPE_PATTERN.test(word),
      builtin: words(intrinsics),
      atoms: words(["true", "false"]),
      // A worksheet line ends without ';': don't read the next line as its continuation
      indentStatements: false,
      hooks: {
        "#": (stream: StringStream, state: { startOfLine: boolean }) => {
          if (!state.startOfLine) {
            return false;
          }
          stream.skipToEnd();
          return "meta";
        },
      },
    }),
  );
}

export const hlslHighlight: HighlightStyle = HighlightStyle.define([
  { tag: tags.comment, color: "var(--syntax-comment)" },
  { tag: tags.keyword, color: "var(--syntax-keyword)" },
  { tag: [tags.typeName, tags.standard(tags.typeName)], color: "var(--syntax-type)" },
  { tag: [tags.standard(tags.variableName), tags.standard(tags.name)], color: "var(--syntax-intrinsic)" },
  { tag: [tags.number, tags.atom, tags.bool], color: "var(--syntax-number)" },
  { tag: tags.string, color: "var(--syntax-string)" },
  { tag: [tags.meta, tags.processingInstruction], color: "var(--syntax-preprocessor)" },
]);
