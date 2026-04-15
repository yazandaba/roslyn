// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Symbols;
using FieldSymbol = Microsoft.CodeAnalysis.CSharp.Symbols.FieldSymbol;

namespace Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// The semantic validator of consteval function
/// </summary>
/// <remarks>
/// 1-  consteval is an ordinary method-only feature in v1 (no constructors, lambdas, accessors, or operators)
/// 2-  parameters type must be primitive (numeric types, bool, char, string, enum, decimal, nint/nuint)
/// 3-  return type must be primitive (numeric types, bool, char, string, enum, decimal, nint/nuint)
/// 4-  not extern
/// 5-  not partial
/// 6-  not virtual/abstract/override/sealed/new
/// 7-  no ref/out/in parameters
/// 8-  not generic, cannot reference any generic-type-dependent object
/// 9-  not async
/// 10- not unsafe
/// 11- no params param
/// 12- no dynamic param or return type
/// 13- local variables are permitted but must also be of primitive type
/// 14- no new expressions of any kind
/// 15- no try/catch/finally, no throw
/// 16- cannot reference class/struct members unless they are const (static readonly is excluded)
/// 17- supported control flow: if, switch, for, while, do-while, break, continue, return
/// 18- recursion is permitted but subject to a compile-time recursion depth limit
/// 19- can only invoke other consteval methods
/// 20- only built-in operators are supported (no user-defined operators)
/// 21- string operations (Length, Concat, etc.) are supported as compiler intrinsics
/// 22- nameof is supported, typeof and sizeof are not (runtime constructs)
/// 23- consteval methods are trimmed from the final binary
/// </remarks>
internal sealed class ConstevalFunctionValidator
{
    private readonly BindingDiagnosticBag _diagnosticBag;

    public ConstevalFunctionValidator(BindingDiagnosticBag diagnosticBag)
    {
        _diagnosticBag = diagnosticBag;
    }

    /// <summary>
    /// Validate consteval method or local function signature against the rules described in the remarks
    /// </summary>
    /// <remarks>
    /// A- consteval applies to ordinary methods and local functions only in v1 (no constructors, lambdas, accessors, or operators)
    /// B- parameters and return type must be primitive (numeric types, bool, char, string, enum, decimal, nint/nuint)
    /// C- no ref/out/in parameters
    /// D- not generic
    /// E- not async
    /// F- not unsafe
    /// G- no params param
    /// H- no dynamic param or return type
    /// I- not extern
    /// J- not partial
    /// K- not virtual/abstract/override/sealed/new (polymorphic modifiers)
    /// </remarks>
    public bool ValidateSignature(MethodSymbol method)
    {
        if (_diagnosticBag.DiagnosticBag is null)
            return true;

        var returnType = method.ReturnType;
        var errNo = _diagnosticBag.DiagnosticBag.Count;

        if (method is not SourceOrdinaryMethodSymbol and not LocalFunctionSymbol)
        {
            return false;
        }

        // 3 - return type must be primitive (numeric types, bool, char, string, enum, decimal, nint/nuint)
        if (returnType is NamedTypeSymbol && !returnType.IsPrimitiveTypeOrEnum())
        {
            _diagnosticBag.Add(ErrorCode.ERR_ConstevalReturnTypeMustBePrimitive, method.GetFirstLocation(), returnType);
        }

        var parameters = method.Parameters;
        foreach (ParameterSymbol parameter in parameters)
        {
            var parameterType = parameter.Type;
            // 2 - parameters must be primitive (numeric types, bool, char, string, enum, decimal, nint/nuint)
            if (!parameterType.IsPrimitiveTypeOrEnum())
            {
                _diagnosticBag.Add(ErrorCode.ERR_ConstevalParameterMustBePrimitive, parameter.GetFirstLocation(), parameter.Name);
            }

            // 7 - no ref/out/in parameters
            if (parameter.RefKind is not RefKind.None)
            {
                // RefKind.In is represented as RefReadOnly in some runtimes; normalize to "In" for the diagnostic.
                var refKindName = parameter.RefKind == RefKind.In ? "In" : parameter.RefKind.ToString();
                _diagnosticBag.Add(ErrorCode.ERR_ConstevalParameterCannotBeRefOutIn, parameter.GetFirstLocation(), parameter.Name, refKindName);
            }

            // 11 - no params param
            if (parameter.IsParams)
            {
                _diagnosticBag.Add(ErrorCode.ERR_ConstevalParameterCannotBeParams, parameter.GetFirstLocation(), parameter.Name);
            }

            // 12 - no dynamic param or return type (parameter check)
            if (parameterType.IsDynamic())
            {
                _diagnosticBag.Add(ErrorCode.ERR_ConstevalParameterCannotBeDynamic, parameter.GetFirstLocation(), parameter.Name);
            }
        }

        // 9 - not async
        if (method.IsAsync)
        {
            _diagnosticBag.Add(ErrorCode.ERR_ConstevalFunctionCannotBeAsync, method.GetFirstLocation(), method.Name);
        }

        // 12 - no dynamic param or return type (return type check)
        if (method.ReturnType.ContainsDynamic())
        {
            _diagnosticBag.Add(ErrorCode.ERR_ConstevalReturnTypeCannotBeDynamic, method.GetFirstLocation());
        }

        // 10 - not unsafe (signature only; unsafe blocks are handled in ValidateBody)
        switch (method)
        {
            case SourceOrdinaryMethodSymbol { IsUnsafe: true }:
            case LocalFunctionSymbol { IsUnsafe: true }:
                _diagnosticBag.Add(ErrorCode.ERR_ConstevalFunctionCannotBeUnsafe, method.GetFirstLocation(), method.Name);
                break;
        }

        // 4 - not extern
        if (method.IsExtern)
        {
            _diagnosticBag.Add(ErrorCode.ERR_ConstevalCannotBeExtern, method.GetFirstLocation(), method.Name);
        }

        // 5 - not partial
        if (method is SourceOrdinaryMethodSymbol { IsPartial: true })
        {
            _diagnosticBag.Add(ErrorCode.ERR_ConstevalCannotBePartial, method.GetFirstLocation(), method.Name);
        }

        // 6 - not virtual/abstract/override/sealed/new
        if (method.IsVirtual || method.IsAbstract || method.IsOverride || method.IsSealed)
        {
            _diagnosticBag.Add(ErrorCode.ERR_ConstevalCannotHavePolymorphicModifiers, method.GetFirstLocation(), method.Name);
        }

        // 8 - not a generic method
        if (method.IsGenericMethod)
        {
            _diagnosticBag.Add(ErrorCode.ERR_ConstevalFunctionCannotBeGeneric, method.GetFirstLocation(), method.Name);
        }

        return _diagnosticBag.DiagnosticBag.Count == errNo;
    }

    /// <summary>
    /// Validate consteval method or local function body against the rules described in the remarks
    /// </summary>
    /// <remarks>
    /// A- not a generic method
    /// B- cannot reference any generic-type-dependent objects
    /// C- local variables must be of primitive type
    /// D- cannot reference class/struct members unless they are const
    /// E- supported control flow: if, switch, for, while, do-while, break, continue, return
    /// F- can only invoke other consteval methods
    /// G- only built-in operators are supported (no user-defined operators)
    /// H- no new expressions of any kind
    /// I- no try/catch/finally, no throw
    /// </remarks>
    public bool ValidateBody(BoundBlock boundBody)
    {
        var checker = new ReferenceBadConstevalConstructs(_diagnosticBag);
        return checker.Check(boundBody);
    }
}

file static class ConstevalExtension
{
    public static bool IsPrimitiveTypeOrEnum(this TypeSymbol type)
    {
        return type.OriginalDefinition.SpecialType switch
        {
            SpecialType.System_Void or
                SpecialType.System_Boolean or
                SpecialType.System_Char or
                SpecialType.System_SByte or
                SpecialType.System_Byte or
                SpecialType.System_Int16 or
                SpecialType.System_UInt16 or
                SpecialType.System_Int32 or
                SpecialType.System_UInt32 or
                SpecialType.System_Int64 or
                SpecialType.System_UInt64 or
                SpecialType.System_Decimal or
                SpecialType.System_Single or
                SpecialType.System_Double or
                SpecialType.System_String or
                SpecialType.System_Enum => true,
            _ => false,
        };
    }
}

file sealed class ReferenceBadConstevalConstructs : BoundTreeWalker
{
    private readonly BindingDiagnosticBag _diagnosticBag;

    public ReferenceBadConstevalConstructs(BindingDiagnosticBag diagnosticBag)
    {
        _diagnosticBag = diagnosticBag;
    }

    public bool Check(BoundNode startingNode)
    {
        if (_diagnosticBag.DiagnosticBag is null)
            return true;

        var errNo = _diagnosticBag.DiagnosticBag.Count;
        startingNode.Accept(this);
        return _diagnosticBag.DiagnosticBag.Count == errNo;
    }

    public override BoundNode? VisitLocalDeclaration(BoundLocalDeclaration node)
    {
        // 13 - local variables are permitted but must also be of primitive type
        if (!node.LocalSymbol.Type.IsPrimitiveTypeOrEnum())
        {
            _diagnosticBag.Add(ErrorCode.ERR_ConstevalLocalMustBePrimitive, node.Syntax.Location, node.LocalSymbol.Name);
        }
        return base.VisitLocalDeclaration(node);
    }

    public override BoundNode? VisitFieldAccess(BoundFieldAccess node)
    {
        FieldSymbol symbol = node.FieldSymbol;
        // 8 - not generic, cannot reference any generic-type-dependent object
        if (symbol.Type.TypeKind is TypeKind.TypeParameter)
        {
            _diagnosticBag.Add(ErrorCode.ERR_ConstevalCannotReferenceGenericType, node.Syntax.Location, symbol.Type);
        }
        // 16 - cannot reference class/struct members unless they are const (static readonly is excluded)
        if (!symbol.IsConst)
        {
            _diagnosticBag.Add(ErrorCode.ERR_ConstevalCanOnlyAccessConstFields, node.Syntax.Location, symbol.Name);
        }
        return base.VisitFieldAccess(node);
    }

    public override BoundNode? VisitTryStatement(BoundTryStatement node)
    {
        // 15 - no try/catch/finally, no throw
        _diagnosticBag.Add(ErrorCode.ERR_ConstevalCannotUseTryCatchFinally, node.Syntax.Location);
        return base.VisitTryStatement(node);
    }

    public override BoundNode? VisitThrowStatement(BoundThrowStatement node)
    {
        // 15 - no try/catch/finally, no throw
        _diagnosticBag.Add(ErrorCode.ERR_ConstevalCannotUseThrow, node.Syntax.Location);
        return base.VisitThrowStatement(node);
    }

    public override BoundNode? VisitSizeOfOperator(BoundSizeOfOperator node)
    {
        // 22 - nameof is supported, typeof and sizeof are not (runtime constructs)
        _diagnosticBag.Add(ErrorCode.ERR_ConstevalCannotUseSizeOf, node.Syntax.Location);
        return base.VisitSizeOfOperator(node);
    }

    public override BoundNode? VisitTypeOfOperator(BoundTypeOfOperator node)
    {
        // 22 - nameof is supported, typeof and sizeof are not (runtime constructs)
        _diagnosticBag.Add(ErrorCode.ERR_ConstevalCannotUseTypeOf, node.Syntax.Location);
        return base.VisitTypeOfOperator(node);
    }

    public override BoundNode? VisitObjectCreationExpression(BoundObjectCreationExpression node)
    {
        // 14 - no new expressions of any kind
        _diagnosticBag.Add(ErrorCode.ERR_ConstevalCannotUseNewExpression, node.Syntax.Location);
        return base.VisitObjectCreationExpression(node);
    }

    public override BoundNode? VisitCall(BoundCall node)
    {
        // 19 - can only invoke other consteval methods
        if (!node.Method.IsConsteval)
        {
            _diagnosticBag.Add(ErrorCode.ERR_ConstevalCanOnlyCallConsteval, node.Syntax.Location, node.Method.Name);
        }
        return base.VisitCall(node);
    }

    public override BoundNode VisitBlock(BoundBlock node)
    {
        // 17 - supported control flow: if, switch, for, while, do-while, break, continue, return
        foreach (BoundStatement statement in node.Statements)
        {
            if (!IsSupportedConstevalStatement(statement))
            {
                _diagnosticBag.Add(ErrorCode.ERR_ConstevalUnsupportedControlFlow, statement.Syntax.Location, statement.Kind.ToString());
            }
            statement.Accept(this);
        }

        return node;
    }

    public override BoundNode? VisitBinaryOperator(BoundBinaryOperator node)
    {
        // 20 - only built-in operators are supported (no user-defined operators)
        // Strip type/lifted/logical bits with .Operator() so IntAddition matches Addition, etc.
        bool isSupported = node.BinaryOperatorMethod is null
            && node.OperatorKind.Operator() is
                BinaryOperatorKind.Addition or
                BinaryOperatorKind.Subtraction or
                BinaryOperatorKind.Multiplication or
                BinaryOperatorKind.Division or
                BinaryOperatorKind.Remainder or
                BinaryOperatorKind.LeftShift or
                BinaryOperatorKind.RightShift or
                BinaryOperatorKind.UnsignedRightShift or
                BinaryOperatorKind.And or
                BinaryOperatorKind.Or or
                BinaryOperatorKind.Xor or
                BinaryOperatorKind.Equal or
                BinaryOperatorKind.NotEqual or
                BinaryOperatorKind.GreaterThan or
                BinaryOperatorKind.GreaterThanOrEqual or
                BinaryOperatorKind.LessThan or
                BinaryOperatorKind.LessThanOrEqual;

        if (!isSupported)
        {
            _diagnosticBag.Add(ErrorCode.ERR_ConstevalCannotUseUserDefinedOperator, node.Syntax.Location);
        }

        return base.VisitBinaryOperator(node);
    }

    public override BoundNode? VisitUnaryOperator(BoundUnaryOperator node)
    {
        // 20 - only built-in operators are supported (no user-defined operators)
        // Strip type/lifted bits with .Operator() so IntUnaryMinus matches UnaryMinus, etc.
        bool isSupported = node.MethodOpt is null
            && node.OperatorKind.Operator() is
                UnaryOperatorKind.BitwiseComplement or
                UnaryOperatorKind.LogicalNegation or
                UnaryOperatorKind.PrefixDecrement or
                UnaryOperatorKind.PostfixDecrement or
                UnaryOperatorKind.PrefixIncrement or
                UnaryOperatorKind.PostfixIncrement or
                UnaryOperatorKind.UnaryPlus or
                UnaryOperatorKind.UnaryMinus;

        if (!isSupported)
        {
            _diagnosticBag.Add(ErrorCode.ERR_ConstevalCannotUseUserDefinedOperator, node.Syntax.Location);
        }

        return base.VisitUnaryOperator(node);
    }

    public override BoundNode? VisitSwitchLabel(BoundSwitchLabel node)
    {
        // Only constant patterns and the discard pattern (default:) are permitted.
        ValidateSwitchPattern(node.Pattern);
        return base.VisitSwitchLabel(node);
    }

    public override BoundNode? VisitSwitchExpressionArm(BoundSwitchExpressionArm node)
    {
        // Only constant patterns and the discard pattern (_) are permitted in switch expressions.
        ValidateSwitchPattern(node.Pattern);
        return base.VisitSwitchExpressionArm(node);
    }

    private void ValidateSwitchPattern(BoundPattern pattern)
    {
        if (pattern is not BoundConstantPattern and not BoundDiscardPattern)
        {
            _diagnosticBag.Add(ErrorCode.ERR_ConstevalSwitchPatternNotAllowed, pattern.Syntax.Location);
        }
    }

    protected override BoundNode? VisitExpressionOrPatternWithoutStackGuard(BoundNode node)
    {
       return node.Accept(this);
    }

    private static bool IsSupportedConstevalStatement(BoundStatement statement)
    {
        return statement switch
        {
            BoundIfStatement or
                BoundSwitchStatement or
                BoundForStatement or
                BoundWhileStatement or
                BoundDoStatement or
                BoundContinueStatement or
                BoundBreakStatement or
                BoundReturnStatement or
                BoundLocalDeclaration or
                BoundMultipleLocalDeclarations or
                BoundExpressionStatement or
                BoundBlock => true,
            _ => false
        };
    }
}
