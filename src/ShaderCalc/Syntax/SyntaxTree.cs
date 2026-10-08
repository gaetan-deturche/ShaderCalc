using ShaderCalc.Units;

namespace ShaderCalc.Syntax;

public abstract record SyntaxNode(SourceSpan Span);

// ---- Types ----

public abstract record TypeSyntax(SourceSpan Span) : SyntaxNode(Span);

/// <summary>A type by name: float3, uint2x2, a struct or a typedef. C++ spellings (unsigned int, std::int32_t) are normalised.</summary>
public sealed record NamedTypeSyntax(string Name, SourceSpan Span) : TypeSyntax(Span);

/// <summary>vector&lt;T, N&gt; or matrix&lt;T, R, C&gt;.</summary>
public sealed record GenericTypeSyntax(string Name, TypeSyntax Element, IReadOnlyList<ExpressionSyntax> Dimensions, SourceSpan Span) : TypeSyntax(Span);

/// <summary>C++ `auto`: the binder takes the initializer's type.</summary>
public sealed record AutoTypeSyntax(SourceSpan Span) : TypeSyntax(Span);

// ---- Expressions ----

public abstract record ExpressionSyntax(SourceSpan Span) : SyntaxNode(Span);

/// <summary>A number or bool literal, already typed: unsuffixed numbers have the literal kinds.</summary>
public sealed record LiteralSyntax(ScalarKind Kind, ulong Bits, string Text, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>Calculator only: `3 km/h`, the number converted to SI with its dimension.</summary>
public sealed record UnitLiteralSyntax(double SiValue, Dimension Dimension, string UnitText, SourceSpan Span) : ExpressionSyntax(Span);

public sealed record NameSyntax(string Name, SourceSpan Span) : ExpressionSyntax(Span);

public sealed record UnaryExpressionSyntax(string Operator, ExpressionSyntax Operand, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>++x, --x, x++, x--.</summary>
public sealed record IncrementSyntax(string Operator, bool IsPrefix, ExpressionSyntax Target, SourceSpan Span) : ExpressionSyntax(Span);

public sealed record BinaryExpressionSyntax(string Operator, ExpressionSyntax Left, ExpressionSyntax Right, SourceSpan Span) : ExpressionSyntax(Span);

public sealed record AssignmentSyntax(string Operator, ExpressionSyntax Target, ExpressionSyntax Value, SourceSpan Span) : ExpressionSyntax(Span);

public sealed record ConditionalSyntax(ExpressionSyntax Condition, ExpressionSyntax WhenTrue, ExpressionSyntax WhenFalse, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>name(args): a user function, an intrinsic, or a constructor like float3(...).</summary>
public sealed record CallSyntax(string Name, SourceSpan NameSpan, IReadOnlyList<ExpressionSyntax> Arguments, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>vector&lt;float, 3&gt;(...).</summary>
public sealed record ConstructorSyntax(TypeSyntax Type, IReadOnlyList<ExpressionSyntax> Arguments, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>(T)x, (T[2])x, static_cast&lt;T&gt;(x).</summary>
public sealed record CastSyntax(TypeSyntax Type, IReadOnlyList<ExpressionSyntax> ArrayDimensions, ExpressionSyntax Operand, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>x.member: a swizzle or a struct field.</summary>
public sealed record MemberSyntax(ExpressionSyntax Target, string Member, SourceSpan MemberSpan, SourceSpan Span) : ExpressionSyntax(Span);

public sealed record IndexSyntax(ExpressionSyntax Target, ExpressionSyntax Index, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>{ a, b, { c } }: flattened when bound, as HLSL does.</summary>
public sealed record InitializerListSyntax(IReadOnlyList<ExpressionSyntax> Elements, SourceSpan Span) : ExpressionSyntax(Span);

// ---- Statements ----

public abstract record StatementSyntax(SourceSpan Span) : SyntaxNode(Span);

public sealed record BlockSyntax(IReadOnlyList<StatementSyntax> Statements, SourceSpan Span) : StatementSyntax(Span);

/// <summary>One declarator: `name[2][3] = init`. A null dimension is `[]` (length taken from the initializer).</summary>
public sealed record DeclaratorSyntax(string Name, SourceSpan NameSpan, IReadOnlyList<ExpressionSyntax?> ArrayDimensions, ExpressionSyntax? Initializer);

[Flags]
public enum StorageModifiers
{
    None = 0,
    Const = 1,
    Static = 2,
    GroupShared = 4,
    Uniform = 8,
}

public sealed record VariableDeclarationSyntax(StorageModifiers Modifiers, TypeSyntax Type, IReadOnlyList<DeclaratorSyntax> Declarators, SourceSpan Span)
    : StatementSyntax(Span);

public sealed record ExpressionStatementSyntax(ExpressionSyntax Expression, SourceSpan Span) : StatementSyntax(Span);

public sealed record IfSyntax(ExpressionSyntax Condition, StatementSyntax Then, StatementSyntax? Else, SourceSpan Span) : StatementSyntax(Span);

public sealed record ForSyntax(StatementSyntax? Initializer, ExpressionSyntax? Condition, ExpressionSyntax? Step, StatementSyntax Body, SourceSpan Span)
    : StatementSyntax(Span);

public sealed record WhileSyntax(ExpressionSyntax Condition, StatementSyntax Body, bool IsDoWhile, SourceSpan Span) : StatementSyntax(Span);

/// <summary>A case label list (null label = default) and the statements that follow.</summary>
public sealed record SwitchSectionSyntax(IReadOnlyList<ExpressionSyntax?> Labels, IReadOnlyList<StatementSyntax> Statements, SourceSpan Span);

public sealed record SwitchSyntax(ExpressionSyntax Value, IReadOnlyList<SwitchSectionSyntax> Sections, SourceSpan Span) : StatementSyntax(Span);

public sealed record ReturnSyntax(ExpressionSyntax? Value, SourceSpan Span) : StatementSyntax(Span);

public sealed record JumpSyntax(string Keyword, SourceSpan Span) : StatementSyntax(Span);

public sealed record EmptyStatementSyntax(SourceSpan Span) : StatementSyntax(Span);

// ---- Declarations ----

public abstract record DeclarationSyntax(SourceSpan Span) : SyntaxNode(Span);

public enum ParameterMode
{
    In,
    Out,
    InOut,
}

public sealed record ParameterSyntax(ParameterMode Mode, bool IsConst, TypeSyntax Type, string Name, SourceSpan NameSpan,
    IReadOnlyList<ExpressionSyntax?> ArrayDimensions, ExpressionSyntax? DefaultValue, SourceSpan Span) : SyntaxNode(Span);

/// <summary>A function definition, or a prototype when <see cref="Body"/> is null.</summary>
public sealed record FunctionSyntax(TypeSyntax ReturnType, string Name, SourceSpan NameSpan, IReadOnlyList<ParameterSyntax> Parameters, BlockSyntax? Body,
    SourceSpan Span) : DeclarationSyntax(Span);

public sealed record StructSyntax(string Name, SourceSpan NameSpan, IReadOnlyList<VariableDeclarationSyntax> Fields, SourceSpan Span) : DeclarationSyntax(Span);

public sealed record TypedefSyntax(TypeSyntax Type, string Name, SourceSpan NameSpan, IReadOnlyList<ExpressionSyntax?> ArrayDimensions, SourceSpan Span)
    : DeclarationSyntax(Span);

/// <summary>A global variable (static const, cbuffer member, uniform...).</summary>
public sealed record GlobalVariableSyntax(VariableDeclarationSyntax Declaration, SourceSpan Span) : DeclarationSyntax(Span);

public sealed record CompilationUnitSyntax(IReadOnlyList<DeclarationSyntax> Declarations);
