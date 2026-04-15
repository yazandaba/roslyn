// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp;

// ---------------------------------------------------------------------------
// Control-flow signal emitted by statement execution
// ---------------------------------------------------------------------------

internal enum ConstevalFlowKind : byte
{
    None,     // Normal completion; execution continues to the next statement
    Return,   // return statement was reached
    Break,    // break statement was reached (exits the current loop/switch)
    Continue, // continue statement was reached (skips to next loop iteration)
    Fault,    // Evaluation failed (e.g. division by zero, depth exceeded)
}

internal readonly struct ConstevalFlow
{
    public static readonly ConstevalFlow None = default;
    public static readonly ConstevalFlow Break = new(ConstevalFlowKind.Break, null);
    public static readonly ConstevalFlow Continue = new(ConstevalFlowKind.Continue, null);
    public static readonly ConstevalFlow Fault = new(ConstevalFlowKind.Fault, null);

    public ConstevalFlowKind Kind { get; }
    public ConstantValue? ReturnValue { get; }

    private ConstevalFlow(ConstevalFlowKind kind, ConstantValue? returnValue)
    {
        Kind = kind;
        ReturnValue = returnValue;
    }

    public static ConstevalFlow Return(ConstantValue? value)
        => new(ConstevalFlowKind.Return, value);

    public bool IsFaulted => Kind == ConstevalFlowKind.Fault;
    public bool IsReturn => Kind == ConstevalFlowKind.Return;
    public bool IsBreak => Kind == ConstevalFlowKind.Break;
    public bool IsContinue => Kind == ConstevalFlowKind.Continue;
    public bool IsNone => Kind == ConstevalFlowKind.None;
}

// ---------------------------------------------------------------------------
// Main interpreter
// ---------------------------------------------------------------------------

/// <summary>
/// Interprets a consteval function body over bound nodes, producing a
/// <see cref="ConstantValue"/> at compile time.
/// </summary>
/// <remarks>
/// <para>
/// Execution model:
/// <list type="bullet">
///   <item>Each method activation gets its own <see cref="EvaluationFrame"/>.</item>
///   <item>A shared <see cref="EvaluationCallStack"/> enforces the recursion depth limit.</item>
///   <item>The interpreter is stateless: no mutable fields beyond the call stack, which
///         is created fresh for every top-level <see cref="TryEvaluate"/> invocation.</item>
/// </list>
/// </para>
/// <para>
/// Supported statements: block, local declaration, expression-statement, if, while,
/// do-while, for, switch, return, break, continue.
/// </para>
/// <para>
/// Supported expressions: literals, locals, parameters, const field reads, binary
/// operators (built-in only), unary operators (built-in only), assignment,
/// compound assignment, pre/post increment/decrement, consteval calls,
/// implicit/explicit numeric conversions, nameof.
/// </para>
/// </remarks>
internal sealed class ConstevalInterpreter
{
    private readonly EvaluationCallStack _callStack;
    private readonly CSharpCompilation _compilation;

    private ConstevalInterpreter(CSharpCompilation compilation)
    {
        _compilation = compilation;
        _callStack = new EvaluationCallStack();
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Evaluates a consteval function call at compile time.
    /// Returns the resulting <see cref="ConstantValue"/>, or <see langword="null"/>
    /// if evaluation is not possible (arguments not all constant, body unavailable, etc.).
    /// Returns <see cref="ConstantValue.Bad"/> and reports a diagnostic if a hard error
    /// (recursion depth exceeded, division by zero, …) occurs during evaluation.
    /// </summary>
    public static ConstantValue? TryEvaluate(
        MethodSymbol method,
        ImmutableArray<ConstantValue> arguments,
        CSharpCompilation compilation,
        Location callSiteLocation,
        BindingDiagnosticBag diagnostics)
    {
        Debug.Assert(method.IsConsteval);

        var interpreter = new ConstevalInterpreter(compilation);
        return interpreter.EvaluateCall(method, arguments, callSiteLocation, diagnostics);
    }

    // -----------------------------------------------------------------------
    // Method evaluation
    // -----------------------------------------------------------------------

    private ConstantValue? EvaluateCall(
        MethodSymbol method,
        ImmutableArray<ConstantValue> arguments,
        Location callSiteLocation,
        BindingDiagnosticBag diagnostics)
    {
        if (_callStack.IsDepthExceeded)
        {
            diagnostics.Add(ErrorCode.ERR_ConstevalRecursionDepthExceeded,
                callSiteLocation, method.Name, EvaluationCallStack.MaxRecursionDepth);
            return ConstantValue.Bad;
        }

        BoundBlock? body = GetBoundBody(method);
        if (body is null)
        {
            // Body is unavailable (e.g. extern, partial declaration without impl).
            // The validator should have caught this; treat as fault silently.
            return null;
        }

        var frame = EvaluationFrame.Create(method.Parameters, arguments, method.Name);

        _callStack.Push(frame);
        try
        {
            ConstevalFlow flow = ExecuteBlock(body, frame, callSiteLocation, diagnostics);

            return flow.Kind switch
            {
                ConstevalFlowKind.Return => flow.ReturnValue,
                ConstevalFlowKind.None => null,   // fell off the end without returning (void method reaching end)
                _ => null,
            };
        }
        finally
        {
            _callStack.Pop();
        }
    }

    // -----------------------------------------------------------------------
    // Body retrieval
    // -----------------------------------------------------------------------

    private BoundBlock? GetBoundBody(MethodSymbol method)
    {
        var discarded = BindingDiagnosticBag.Discarded;

        switch (method)
        {
            case SourceMemberMethodSymbol sourceMember:
                {
                    var bodyBinder = sourceMember.TryGetBodyBinder();
                    if (bodyBinder is null)
                        return null;

                    var methodBody = bodyBinder.BindMethodBody(sourceMember.SyntaxNode, discarded);
                    return methodBody switch
                    {
                        BoundNonConstructorMethodBody nonCtor => nonCtor.BlockBody ?? nonCtor.ExpressionBody,
                        BoundBlock block => block,
                        _ => null,
                    };
                }
            case LocalFunctionSymbol localFn:
                {
                    var syntax = localFn.Syntax;
                    var binderFactory = _compilation.GetBinderFactory(syntax.SyntaxTree);

                    if (syntax.Body != null)
                    {
                        // Wrap the outer scope binder with InMethodBinder to put the local function's
                        // parameters (and return-type context) in scope for the body.
                        var outerBinder = binderFactory.GetBinder(syntax.Body);
                        var inMethodBinder = new InMethodBinder(localFn, outerBinder);
                        return inMethodBinder.BindEmbeddedBlock(syntax.Body, discarded);
                    }

                    if (syntax.ExpressionBody != null)
                    {
                        // Same idea: wrap the outer binder with InMethodBinder so that parameters
                        // are visible, then bind the arrow expression directly and wrap in a block.
                        var outerBinder = binderFactory.GetBinder(syntax.ExpressionBody);
                        var inMethodBinder = new InMethodBinder(localFn, outerBinder);
                        var expr = inMethodBinder.BindExpression(syntax.ExpressionBody.Expression, discarded);
                        var returnStmt = new BoundReturnStatement(syntax.ExpressionBody, RefKind.None, expr, @checked: false);
                        return new BoundBlock(syntax,
                            ImmutableArray<LocalSymbol>.Empty,
                            ImmutableArray<MethodSymbol>.Empty,
                            hasUnsafeModifier: false,
                            instrumentation: null,
                            ImmutableArray.Create<BoundStatement>(returnStmt));
                    }

                    break;
                }
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Statement execution
    // -----------------------------------------------------------------------

    private ConstevalFlow ExecuteBlock(
        BoundBlock block,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        foreach (BoundStatement statement in block.Statements)
        {
            ConstevalFlow flow = ExecuteStatement(statement, frame, location, diagnostics);
            if (!flow.IsNone)
                return flow;
        }

        return ConstevalFlow.None;
    }

    private ConstevalFlow ExecuteStatement(
        BoundStatement statement,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        return statement switch
        {
            BoundBlock block
                => ExecuteBlock(block, frame, location, diagnostics),

            BoundLocalDeclaration localDecl
                => ExecuteLocalDeclaration(localDecl, frame, location, diagnostics),

            BoundMultipleLocalDeclarations multiDecl
                => ExecuteMultipleLocalDeclarations(multiDecl, frame, location, diagnostics),

            BoundExpressionStatement exprStmt
                => ExecuteExpressionStatement(exprStmt, frame, location, diagnostics),

            BoundIfStatement ifStmt
                => ExecuteIf(ifStmt, frame, location, diagnostics),

            BoundWhileStatement whileStmt
                => ExecuteWhile(whileStmt, frame, location, diagnostics),

            BoundDoStatement doStmt
                => ExecuteDo(doStmt, frame, location, diagnostics),

            BoundForStatement forStmt
                => ExecuteFor(forStmt, frame, location, diagnostics),

            BoundSwitchStatement switchStmt
                => ExecuteSwitch(switchStmt, frame, location, diagnostics),

            BoundReturnStatement ret
                => ExecuteReturn(ret, frame, location, diagnostics),

            BoundBreakStatement
                => ConstevalFlow.Break,

            BoundContinueStatement
                => ConstevalFlow.Continue,

            // Any other statement kind was rejected by the validator; treat as fault.
            _ => ConstevalFlow.Fault,
        };
    }

    private ConstevalFlow ExecuteLocalDeclaration(
        BoundLocalDeclaration decl,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        if (decl.InitializerOpt is not null)
        {
            ConstantValue? initVal = EvaluateExpression(decl.InitializerOpt, frame, location, diagnostics);
            if (initVal is null)
                return ConstevalFlow.Fault;
            frame.SetLocal(decl.LocalSymbol, initVal);
        }

        return ConstevalFlow.None;
    }

    private ConstevalFlow ExecuteMultipleLocalDeclarations(
        BoundMultipleLocalDeclarations multiDecl,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        foreach (BoundLocalDeclaration decl in multiDecl.LocalDeclarations)
        {
            ConstevalFlow flow = ExecuteLocalDeclaration(decl, frame, location, diagnostics);
            if (!flow.IsNone)
                return flow;
        }

        return ConstevalFlow.None;
    }

    private ConstevalFlow ExecuteExpressionStatement(
        BoundExpressionStatement stmt,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        // Evaluate for side effects (assignments, consteval calls, increment/decrement).
        ConstantValue? result = EvaluateExpression(stmt.Expression, frame, location, diagnostics);

        // A null result for a non-void expression-statement indicates a fault.
        if (result is null && stmt.Expression.Type?.SpecialType != SpecialType.System_Void)
            return ConstevalFlow.Fault;

        return ConstevalFlow.None;
    }

    private ConstevalFlow ExecuteIf(
        BoundIfStatement ifStmt,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        ConstantValue? condVal = EvaluateExpression(ifStmt.Condition, frame, location, diagnostics);
        if (condVal is null || condVal.IsBad)
            return ConstevalFlow.Fault;

        bool cond = condVal.BooleanValue;
        if (cond)
            return ExecuteStatement(ifStmt.Consequence, frame, location, diagnostics);
        if (ifStmt.AlternativeOpt is not null)
            return ExecuteStatement(ifStmt.AlternativeOpt, frame, location, diagnostics);

        return ConstevalFlow.None;
    }

    private ConstevalFlow ExecuteWhile(
        BoundWhileStatement whileStmt,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        int iterations = 0;
        while (true)
        {
            ConstantValue? condVal = EvaluateExpression(whileStmt.Condition, frame, location, diagnostics);
            if (condVal is null || condVal.IsBad)
                return ConstevalFlow.Fault;

            if (!condVal.BooleanValue)
                break;

            if (++iterations > EvaluationCallStack.MaxLoopIterations)
            {
                diagnostics.Add(ErrorCode.ERR_ConstevalLoopIterationLimitExceeded,
                    location, EvaluationCallStack.MaxLoopIterations);
                return ConstevalFlow.Fault;
            }

            ConstevalFlow bodyFlow = ExecuteStatement(whileStmt.Body, frame, location, diagnostics);
            if (bodyFlow.IsBreak) break;
            if (bodyFlow.IsReturn || bodyFlow.IsFaulted) return bodyFlow;
            // IsContinue → skip to next iteration (normal loop behavior)
        }

        return ConstevalFlow.None;
    }

    private ConstevalFlow ExecuteDo(
        BoundDoStatement doStmt,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        int iterations = 0;
        do
        {
            if (++iterations > EvaluationCallStack.MaxLoopIterations)
            {
                diagnostics.Add(ErrorCode.ERR_ConstevalLoopIterationLimitExceeded,
                    location, EvaluationCallStack.MaxLoopIterations);
                return ConstevalFlow.Fault;
            }

            ConstevalFlow bodyFlow = ExecuteStatement(doStmt.Body, frame, location, diagnostics);
            if (bodyFlow.IsBreak) break;
            if (bodyFlow.IsReturn || bodyFlow.IsFaulted) return bodyFlow;

            ConstantValue? condVal = EvaluateExpression(doStmt.Condition, frame, location, diagnostics);
            if (condVal is null || condVal.IsBad)
                return ConstevalFlow.Fault;

            if (!condVal.BooleanValue)
                break;
        }
        while (true);

        return ConstevalFlow.None;
    }

    private ConstevalFlow ExecuteFor(
        BoundForStatement forStmt,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        // Execute initializer (may be a declaration or expression statement)
        if (forStmt.Initializer is not null)
        {
            ConstevalFlow initFlow = ExecuteStatement(forStmt.Initializer, frame, location, diagnostics);
            if (!initFlow.IsNone)
                return initFlow;
        }

        int iterations = 0;
        while (true)
        {
            // Evaluate condition; an absent condition is always true (infinite loop until break/return)
            if (forStmt.Condition is not null)
            {
                ConstantValue? condVal = EvaluateExpression(forStmt.Condition, frame, location, diagnostics);
                if (condVal is null || condVal.IsBad)
                    return ConstevalFlow.Fault;

                if (!condVal.BooleanValue)
                    break;
            }

            if (++iterations > EvaluationCallStack.MaxLoopIterations)
            {
                diagnostics.Add(ErrorCode.ERR_ConstevalLoopIterationLimitExceeded,
                    location, EvaluationCallStack.MaxLoopIterations);
                return ConstevalFlow.Fault;
            }

            ConstevalFlow bodyFlow = ExecuteStatement(forStmt.Body, frame, location, diagnostics);
            if (bodyFlow.IsBreak) break;
            if (bodyFlow.IsReturn || bodyFlow.IsFaulted) return bodyFlow;
            // IsContinue → fall through to increment

            // Execute increment
            if (forStmt.Increment is not null)
            {
                ConstevalFlow incrFlow = ExecuteStatement(forStmt.Increment, frame, location, diagnostics);
                if (!incrFlow.IsNone)
                    return incrFlow;
            }
        }

        return ConstevalFlow.None;
    }

    private ConstevalFlow ExecuteSwitch(
        BoundSwitchStatement switchStmt,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        ConstantValue? switchVal = EvaluateExpression(switchStmt.Expression, frame, location, diagnostics);
        if (switchVal is null || switchVal.IsBad)
            return ConstevalFlow.Fault;

        // Linear scan through sections for a matching label.
        BoundSwitchSection? matchedSection = null;
        BoundSwitchSection? defaultSection = null;

        foreach (BoundSwitchSection section in switchStmt.SwitchSections)
        {
            foreach (BoundSwitchLabel label in section.SwitchLabels)
            {
                if (label.Pattern is BoundDiscardPattern)
                {
                    // 'default:' label
                    defaultSection = section;
                    continue;
                }

                if (label.Pattern is BoundConstantPattern { ConstantValue: { } labelConst })
                {
                    bool matches = switchVal.Discriminator == labelConst.Discriminator
                                   && switchVal.Equals(labelConst);

                    if (matches)
                    {
                        // Evaluate optional when clause
                        if (label.WhenClause is not null)
                        {
                            ConstantValue? whenVal = EvaluateExpression(label.WhenClause, frame, location, diagnostics);
                            if (whenVal is null || whenVal.IsBad)
                                return ConstevalFlow.Fault;
                            if (!whenVal.BooleanValue)
                                continue;
                        }

                        matchedSection = section;
                        break;
                    }
                }
                // Other pattern kinds (type, relational, …) are not expected in consteval;
                // the validator should have rejected them.
            }

            if (matchedSection is not null)
                break;
        }

        BoundSwitchSection? toExecute = matchedSection ?? defaultSection;
        if (toExecute is null)
            return ConstevalFlow.None; // no matching case; fall out of switch

        foreach (BoundStatement stmt in toExecute.Statements)
        {
            ConstevalFlow stmtFlow = ExecuteStatement(stmt, frame, location, diagnostics);
            if (stmtFlow.IsBreak) return ConstevalFlow.None; // break exits the switch
            if (!stmtFlow.IsNone) return stmtFlow;
        }

        return ConstevalFlow.None;
    }

    // -----------------------------------------------------------------------
    // Switch expression evaluation
    // -----------------------------------------------------------------------

    private ConstantValue? EvaluateSwitchExpression(
        BoundConvertedSwitchExpression switchExpr,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        ConstantValue? switchVal = EvaluateExpression(switchExpr.Expression, frame, location, diagnostics);
        if (switchVal is null)
            return null;
        if (switchVal.IsBad)
            return ConstantValue.Bad;

        BoundSwitchExpressionArm? defaultArm = null;

        foreach (BoundSwitchExpressionArm arm in switchExpr.SwitchArms)
        {
            if (arm.Pattern is BoundDiscardPattern)
            {
                // '_' arm — save as the default, keep scanning for a more specific match.
                defaultArm = arm;
                continue;
            }

            if (arm.Pattern is BoundConstantPattern { ConstantValue: { } labelConst })
            {
                bool matches = switchVal.Discriminator == labelConst.Discriminator
                               && switchVal.Equals(labelConst);

                if (matches)
                {
                    if (arm.WhenClause is not null)
                    {
                        ConstantValue? whenVal = EvaluateExpression(arm.WhenClause, frame, location, diagnostics);
                        if (whenVal is null)
                            return null;
                        if (whenVal.IsBad)
                            return ConstantValue.Bad;
                        if (!whenVal.BooleanValue)
                            continue; // when clause false: keep scanning
                    }

                    return EvaluateExpression(arm.Value, frame, location, diagnostics);
                }
            }
            // Non-constant patterns are rejected by the validator; skip silently.
        }

        // Fall through to the default arm if present.
        if (defaultArm is not null)
        {
            if (defaultArm.WhenClause is not null)
            {
                ConstantValue? whenVal = EvaluateExpression(defaultArm.WhenClause, frame, location, diagnostics);
                if (whenVal is null)
                    return null;
                if (whenVal.IsBad)
                    return ConstantValue.Bad;
                if (whenVal.BooleanValue)
                    return EvaluateExpression(defaultArm.Value, frame, location, diagnostics);
            }
            else
            {
                return EvaluateExpression(defaultArm.Value, frame, location, diagnostics);
            }
        }

        // No matching arm (non-exhaustive switch expression — should not happen in valid consteval).
        return null;
    }

    private ConstevalFlow ExecuteReturn(
        BoundReturnStatement ret,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        if (ret.ExpressionOpt is null)
            return ConstevalFlow.Return(null);

        ConstantValue? value = EvaluateExpression(ret.ExpressionOpt, frame, location, diagnostics);
        if (value is null)
            return ConstevalFlow.Fault;

        return ConstevalFlow.Return(value);
    }

    // -----------------------------------------------------------------------
    // Expression evaluation
    // -----------------------------------------------------------------------

    private ConstantValue? EvaluateExpression(
        BoundExpression expr,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        // Fast path: the binder already folded this expression to a constant.
        if (expr.ConstantValueOpt is { } precomputed && !precomputed.IsBad)
            return precomputed;

        return expr switch
        {
            BoundLiteral lit
                => lit.ConstantValueOpt,

            BoundLocal local
                => frame.TryGetLocal(local.LocalSymbol)
                   ?? local.ConstantValueOpt, // compile-time const local

            BoundParameter param
                => frame.TryGetParameter(param.ParameterSymbol),

            BoundFieldAccess { FieldSymbol.IsConst: true } field
                => field.FieldSymbol.GetConstantValue(ConstantFieldsInProgress.Empty, earlyDecodingWellKnownAttributes: false),

            BoundBinaryOperator binOp
                => EvaluateBinaryOperator(binOp, frame, location, diagnostics),

            BoundUnaryOperator unOp
                => EvaluateUnaryOperator(unOp, frame, location, diagnostics),

            BoundAssignmentOperator assign
                => EvaluateAssignment(assign, frame, location, diagnostics),

            BoundCompoundAssignmentOperator compound
                => EvaluateCompoundAssignment(compound, frame, location, diagnostics),

            BoundIncrementOperator incr
                => EvaluateIncrement(incr, frame, location, diagnostics),

            BoundCall { Method.IsConsteval: true } call
                => EvaluateConstevalCall(call, frame, location, diagnostics),

            BoundConversion conv
                => EvaluateConversion(conv, frame, location, diagnostics),

            BoundConditionalOperator cond
                => EvaluateConditional(cond, frame, location, diagnostics),

            BoundConvertedSwitchExpression switchExpr
                => EvaluateSwitchExpression(switchExpr, frame, location, diagnostics),

            BoundNameOfOperator nameOf
                => nameOf.ConstantValueOpt,

            // Expression-bodied methods/lambdas produce a BoundSequence with a value;
            // not expected in consteval body, but handle gracefully.
            _ => null,
        };
    }

    private ConstantValue? EvaluateConditional(
        BoundConditionalOperator cond,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        ConstantValue? condVal = EvaluateExpression(cond.Condition, frame, location, diagnostics);
        if (condVal is null) return null;
        if (condVal.IsBad) return ConstantValue.Bad;

        return condVal.BooleanValue
            ? EvaluateExpression(cond.Consequence, frame, location, diagnostics)
            : EvaluateExpression(cond.Alternative, frame, location, diagnostics);
    }

    private ConstantValue? EvaluateBinaryOperator(
        BoundBinaryOperator op,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        ConstantValue? left = EvaluateExpression(op.Left, frame, location, diagnostics);
        ConstantValue? right = EvaluateExpression(op.Right, frame, location, diagnostics);

        if (left is null || right is null)
            return null;
        if (left.IsBad || right.IsBad)
            return ConstantValue.Bad;

        // Delegate to static computation (avoids capturing instance state).
        return ApplyBinaryOperator(op.OperatorKind, left, right, op.Type.SpecialType, location, diagnostics);
    }

    private ConstantValue? EvaluateUnaryOperator(
        BoundUnaryOperator op,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        ConstantValue? operand = EvaluateExpression(op.Operand, frame, location, diagnostics);
        if (operand is null)
            return null;
        if (operand.IsBad)
            return ConstantValue.Bad;

        return ApplyUnaryOperator(op.OperatorKind, operand);
    }

    private ConstantValue? EvaluateAssignment(
        BoundAssignmentOperator assign,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        ConstantValue? value = EvaluateExpression(assign.Right, frame, location, diagnostics);
        if (value is null)
            return null;

        if (assign.Left is BoundLocal local)
            frame.SetLocal(local.LocalSymbol, value);
        else if (assign.Left is BoundParameter param)
            frame.SetParameter(param.ParameterSymbol, value);

        return value; // assignment is an expression; it yields the assigned value
    }

    private ConstantValue? EvaluateCompoundAssignment(
        BoundCompoundAssignmentOperator compound,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        ConstantValue? currentVal = EvaluateExpression(compound.Left, frame, location, diagnostics);
        ConstantValue? rhsVal = EvaluateExpression(compound.Right, frame, location, diagnostics);

        if (currentVal is null || rhsVal is null)
            return null;
        if (currentVal.IsBad || rhsVal.IsBad)
            return ConstantValue.Bad;

        ConstantValue? result = ApplyBinaryOperator(
            compound.Operator.Kind,
            currentVal,
            rhsVal,
            compound.Type.SpecialType,
            location,
            diagnostics);

        if (result is null)
            return null;

        if (compound.Left is BoundLocal local)
            frame.SetLocal(local.LocalSymbol, result);
        else if (compound.Left is BoundParameter param)
            frame.SetParameter(param.ParameterSymbol, result);

        return result;
    }

    private ConstantValue? EvaluateIncrement(
        BoundIncrementOperator incr,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        ConstantValue? current = EvaluateExpression(incr.Operand, frame, location, diagnostics);
        if (current is null)
            return null;
        if (current.IsBad)
            return ConstantValue.Bad;

        UnaryOperatorKind opKind = incr.OperatorKind & ~(UnaryOperatorKind.Lifted | UnaryOperatorKind.Checked);
        bool isPrefix = (opKind & UnaryOperatorKind.OpMask) is
            UnaryOperatorKind.PrefixIncrement or UnaryOperatorKind.PrefixDecrement;
        bool isIncrement = (opKind & UnaryOperatorKind.OpMask) is
            UnaryOperatorKind.PrefixIncrement or UnaryOperatorKind.PostfixIncrement;

        ConstantValue one = ConstantValue.Create(1);
        ConstantValue? newVal = isIncrement
            ? AddOne(current, one)
            : SubtractOne(current, one);

        if (newVal is null || newVal.IsBad)
            return newVal ?? ConstantValue.Bad;

        if (incr.Operand is BoundLocal local)
            frame.SetLocal(local.LocalSymbol, newVal);
        else if (incr.Operand is BoundParameter param)
            frame.SetParameter(param.ParameterSymbol, newVal);

        // Prefix: return new value; postfix: return old value.
        return isPrefix ? newVal : current;
    }

    private ConstantValue? EvaluateConstevalCall(
        BoundCall call,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        // Evaluate all arguments to constant values.
        var argBuilder = ImmutableArray.CreateBuilder<ConstantValue>(call.Arguments.Length);
        foreach (BoundExpression arg in call.Arguments)
        {
            ConstantValue? argVal = EvaluateExpression(arg, frame, location, diagnostics);
            if (argVal is null)
                return null;
            if (argVal.IsBad)
                return ConstantValue.Bad;
            argBuilder.Add(argVal);
        }

        return EvaluateCall(call.Method, argBuilder.ToImmutable(), location, diagnostics);
    }

    private ConstantValue? EvaluateConversion(
        BoundConversion conv,
        EvaluationFrame frame,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        ConstantValue? operand = EvaluateExpression(conv.Operand, frame, location, diagnostics);
        if (operand is null)
            return null;
        if (operand.IsBad)
            return ConstantValue.Bad;

        ConversionKind kind = conv.Conversion.Kind;

        if (kind is ConversionKind.Identity or ConversionKind.ImplicitReference)
            return operand;

        if (kind is ConversionKind.ImplicitNumeric or ConversionKind.ExplicitNumeric
                 or ConversionKind.ImplicitConstant
                 or ConversionKind.ImplicitEnumeration or ConversionKind.ExplicitEnumeration
                 or ConversionKind.ExplicitUserDefined)  // enum→underlying falls here sometimes
        {
            return ConvertToSpecialType(operand, conv.Type.SpecialType);
        }

        if (kind is ConversionKind.NullLiteral or ConversionKind.DefaultLiteral)
            return ConstantValue.Null;

        // Other conversions (boxing, user-defined operator, …) are not expected in consteval.
        return null;
    }

    // -----------------------------------------------------------------------
    // Arithmetic helpers – increment/decrement by 1
    // -----------------------------------------------------------------------

    private static ConstantValue? AddOne(ConstantValue v, ConstantValue _)
    {
        try
        {
            return v.Discriminator switch
            {
                ConstantValueTypeDiscriminator.SByte => ConstantValue.Create(unchecked((sbyte)(v.SByteValue + 1))),
                ConstantValueTypeDiscriminator.Byte => ConstantValue.Create(unchecked((byte)(v.ByteValue + 1))),
                ConstantValueTypeDiscriminator.Int16 => ConstantValue.Create(unchecked((short)(v.Int16Value + 1))),
                ConstantValueTypeDiscriminator.UInt16 => ConstantValue.Create(unchecked((ushort)(v.UInt16Value + 1))),
                ConstantValueTypeDiscriminator.Int32 => ConstantValue.Create(unchecked(v.Int32Value + 1)),
                ConstantValueTypeDiscriminator.UInt32 => ConstantValue.Create(unchecked(v.UInt32Value + 1u)),
                ConstantValueTypeDiscriminator.Int64 => ConstantValue.Create(unchecked(v.Int64Value + 1L)),
                ConstantValueTypeDiscriminator.UInt64 => ConstantValue.Create(unchecked(v.UInt64Value + 1UL)),
                ConstantValueTypeDiscriminator.Single => ConstantValue.Create(v.SingleValue + 1f),
                ConstantValueTypeDiscriminator.Double => ConstantValue.Create(v.DoubleValue + 1.0),
                ConstantValueTypeDiscriminator.Decimal => ConstantValue.Create(v.DecimalValue + 1m),
                ConstantValueTypeDiscriminator.Char => ConstantValue.Create(unchecked((char)(v.CharValue + 1))),
                _ => null,
            };
        }
        catch (OverflowException)
        {
            return ConstantValue.Bad;
        }
    }

    private static ConstantValue? SubtractOne(ConstantValue v, ConstantValue _)
    {
        try
        {
            return v.Discriminator switch
            {
                ConstantValueTypeDiscriminator.SByte => ConstantValue.Create(unchecked((sbyte)(v.SByteValue - 1))),
                ConstantValueTypeDiscriminator.Byte => ConstantValue.Create(unchecked((byte)(v.ByteValue - 1))),
                ConstantValueTypeDiscriminator.Int16 => ConstantValue.Create(unchecked((short)(v.Int16Value - 1))),
                ConstantValueTypeDiscriminator.UInt16 => ConstantValue.Create(unchecked((ushort)(v.UInt16Value - 1))),
                ConstantValueTypeDiscriminator.Int32 => ConstantValue.Create(unchecked(v.Int32Value - 1)),
                ConstantValueTypeDiscriminator.UInt32 => ConstantValue.Create(unchecked(v.UInt32Value - 1u)),
                ConstantValueTypeDiscriminator.Int64 => ConstantValue.Create(unchecked(v.Int64Value - 1L)),
                ConstantValueTypeDiscriminator.UInt64 => ConstantValue.Create(unchecked(v.UInt64Value - 1UL)),
                ConstantValueTypeDiscriminator.Single => ConstantValue.Create(v.SingleValue - 1f),
                ConstantValueTypeDiscriminator.Double => ConstantValue.Create(v.DoubleValue - 1.0),
                ConstantValueTypeDiscriminator.Decimal => ConstantValue.Create(v.DecimalValue - 1m),
                ConstantValueTypeDiscriminator.Char => ConstantValue.Create(unchecked((char)(v.CharValue - 1))),
                _ => null,
            };
        }
        catch (OverflowException)
        {
            return ConstantValue.Bad;
        }
    }

    // -----------------------------------------------------------------------
    // Binary operator application
    // -----------------------------------------------------------------------

    private ConstantValue? ApplyBinaryOperator(
        BinaryOperatorKind kind,
        ConstantValue left,
        ConstantValue right,
        SpecialType resultType,
        Location location,
        BindingDiagnosticBag diagnostics)
    {
        // Strip lifted / logical / checked flags; consteval runs in unchecked context by default.
        BinaryOperatorKind baseKind = kind & ~(BinaryOperatorKind.Lifted | BinaryOperatorKind.Logical | BinaryOperatorKind.Checked);

        var frame = _callStack.CurrentFrame;
        Debug.Assert(frame is not null);

        // Guard against division by zero for integer/decimal types.
        if (IsDivisionByZero(baseKind, right))
        {
            diagnostics.Add(ErrorCode.ERR_ConstevalDivisionByZero, location, frame.FrameName);
            return ConstantValue.Bad;
        }

        try
        {
            object? result = ComputeBinaryOperator(baseKind, left, right);
            if (result is null)
                return null;

            return ConstantValue.Create(result, resultType);
        }
        catch (OverflowException)
        {
            // Overflow in unchecked context wraps; if we hit here it's from decimal.
            diagnostics.Add(ErrorCode.ERR_ConstevalOverflow, location, frame.FrameName);
            return ConstantValue.Bad;
        }
        catch (DivideByZeroException)
        {
            diagnostics.Add(ErrorCode.ERR_ConstevalDivisionByZero, location, frame.FrameName);
            return ConstantValue.Bad;
        }
    }

    private static bool IsDivisionByZero(BinaryOperatorKind kind, ConstantValue right)
    {
        BinaryOperatorKind op = kind & BinaryOperatorKind.OpMask;
        if (op is not (BinaryOperatorKind.Division or BinaryOperatorKind.Remainder))
            return false;

        BinaryOperatorKind type = kind & BinaryOperatorKind.TypeMask;
        return type switch
        {
            BinaryOperatorKind.Int or BinaryOperatorKind.NInt => right.Int32Value == 0,
            BinaryOperatorKind.UInt or BinaryOperatorKind.NUInt => right.UInt32Value == 0,
            BinaryOperatorKind.Long => right.Int64Value == 0,
            BinaryOperatorKind.ULong => right.UInt64Value == 0,
            BinaryOperatorKind.Decimal => right.DecimalValue == 0m,
            _ => false,
        };
    }

    // NOTE: ConstantValue.Create(object, SpecialType) is used by the caller.
    // This method returns a boxed CLR primitive that maps to the result type.
#pragma warning disable CS0162 // Unreachable code detected (switch exhaustiveness)
    private static object? ComputeBinaryOperator(BinaryOperatorKind kind, ConstantValue left, ConstantValue right)
    {
        return kind switch
        {
            // ── Multiplication ─────────────────────────────────────────────
            BinaryOperatorKind.IntMultiplication or BinaryOperatorKind.NIntMultiplication
                => unchecked(left.Int32Value * right.Int32Value),
            BinaryOperatorKind.UIntMultiplication or BinaryOperatorKind.NUIntMultiplication
                => unchecked(left.UInt32Value * right.UInt32Value),
            BinaryOperatorKind.LongMultiplication
                => unchecked(left.Int64Value * right.Int64Value),
            BinaryOperatorKind.ULongMultiplication
                => unchecked(left.UInt64Value * right.UInt64Value),
            BinaryOperatorKind.FloatMultiplication
                => left.SingleValue * right.SingleValue,
            BinaryOperatorKind.DoubleMultiplication
                => left.DoubleValue * right.DoubleValue,
            BinaryOperatorKind.DecimalMultiplication
                => left.DecimalValue * right.DecimalValue,

            // ── Addition ───────────────────────────────────────────────────
            BinaryOperatorKind.IntAddition or BinaryOperatorKind.NIntAddition
                => unchecked(left.Int32Value + right.Int32Value),
            BinaryOperatorKind.UIntAddition or BinaryOperatorKind.NUIntAddition
                => unchecked(left.UInt32Value + right.UInt32Value),
            BinaryOperatorKind.LongAddition
                => unchecked(left.Int64Value + right.Int64Value),
            BinaryOperatorKind.ULongAddition
                => unchecked(left.UInt64Value + right.UInt64Value),
            BinaryOperatorKind.FloatAddition
                => left.SingleValue + right.SingleValue,
            BinaryOperatorKind.DoubleAddition
                => left.DoubleValue + right.DoubleValue,
            BinaryOperatorKind.DecimalAddition
                => left.DecimalValue + right.DecimalValue,
            BinaryOperatorKind.StringConcatenation
                => (left.StringValue ?? "") + (right.StringValue ?? ""),

            // ── Subtraction ────────────────────────────────────────────────
            BinaryOperatorKind.IntSubtraction or BinaryOperatorKind.NIntSubtraction
                => unchecked(left.Int32Value - right.Int32Value),
            BinaryOperatorKind.UIntSubtraction or BinaryOperatorKind.NUIntSubtraction
                => unchecked(left.UInt32Value - right.UInt32Value),
            BinaryOperatorKind.LongSubtraction
                => unchecked(left.Int64Value - right.Int64Value),
            BinaryOperatorKind.ULongSubtraction
                => unchecked(left.UInt64Value - right.UInt64Value),
            BinaryOperatorKind.FloatSubtraction
                => left.SingleValue - right.SingleValue,
            BinaryOperatorKind.DoubleSubtraction
                => left.DoubleValue - right.DoubleValue,
            BinaryOperatorKind.DecimalSubtraction
                => left.DecimalValue - right.DecimalValue,

            // ── Division ───────────────────────────────────────────────────
            BinaryOperatorKind.IntDivision or BinaryOperatorKind.NIntDivision
                => left.Int32Value / right.Int32Value,
            BinaryOperatorKind.UIntDivision or BinaryOperatorKind.NUIntDivision
                => left.UInt32Value / right.UInt32Value,
            BinaryOperatorKind.LongDivision
                => left.Int64Value / right.Int64Value,
            BinaryOperatorKind.ULongDivision
                => left.UInt64Value / right.UInt64Value,
            BinaryOperatorKind.FloatDivision
                => left.SingleValue / right.SingleValue,
            BinaryOperatorKind.DoubleDivision
                => left.DoubleValue / right.DoubleValue,
            BinaryOperatorKind.DecimalDivision
                => left.DecimalValue / right.DecimalValue,

            // ── Remainder ──────────────────────────────────────────────────
            BinaryOperatorKind.IntRemainder or BinaryOperatorKind.NIntRemainder
                => left.Int32Value % right.Int32Value,
            BinaryOperatorKind.UIntRemainder or BinaryOperatorKind.NUIntRemainder
                => left.UInt32Value % right.UInt32Value,
            BinaryOperatorKind.LongRemainder
                => left.Int64Value % right.Int64Value,
            BinaryOperatorKind.ULongRemainder
                => left.UInt64Value % right.UInt64Value,
            BinaryOperatorKind.FloatRemainder
                => left.SingleValue % right.SingleValue,
            BinaryOperatorKind.DoubleRemainder
                => left.DoubleValue % right.DoubleValue,
            BinaryOperatorKind.DecimalRemainder
                => left.DecimalValue % right.DecimalValue,

            // ── Shifts ─────────────────────────────────────────────────────
            BinaryOperatorKind.IntLeftShift or BinaryOperatorKind.NIntLeftShift
                => left.Int32Value << right.Int32Value,
            BinaryOperatorKind.UIntLeftShift or BinaryOperatorKind.NUIntLeftShift
                => left.UInt32Value << right.Int32Value,
            BinaryOperatorKind.LongLeftShift
                => left.Int64Value << right.Int32Value,
            BinaryOperatorKind.ULongLeftShift
                => left.UInt64Value << right.Int32Value,
            BinaryOperatorKind.IntRightShift or BinaryOperatorKind.NIntRightShift
                => left.Int32Value >> right.Int32Value,
            BinaryOperatorKind.UIntRightShift or BinaryOperatorKind.NUIntRightShift
                => left.UInt32Value >> right.Int32Value,
            BinaryOperatorKind.LongRightShift
                => left.Int64Value >> right.Int32Value,
            BinaryOperatorKind.ULongRightShift
                => left.UInt64Value >> right.Int32Value,
            BinaryOperatorKind.IntUnsignedRightShift or BinaryOperatorKind.NIntUnsignedRightShift
                => left.Int32Value >>> right.Int32Value,
            BinaryOperatorKind.LongUnsignedRightShift
                => left.Int64Value >>> right.Int32Value,
            BinaryOperatorKind.UIntUnsignedRightShift or BinaryOperatorKind.NUIntUnsignedRightShift
                => left.UInt32Value >> right.Int32Value,
            BinaryOperatorKind.ULongUnsignedRightShift
                => left.UInt64Value >> right.Int32Value,

            // ── Bitwise AND ────────────────────────────────────────────────
            BinaryOperatorKind.BoolAnd => left.BooleanValue & right.BooleanValue,
            BinaryOperatorKind.IntAnd or BinaryOperatorKind.NIntAnd
                => left.Int32Value & right.Int32Value,
            BinaryOperatorKind.UIntAnd or BinaryOperatorKind.NUIntAnd
                => left.UInt32Value & right.UInt32Value,
            BinaryOperatorKind.LongAnd => left.Int64Value & right.Int64Value,
            BinaryOperatorKind.ULongAnd => left.UInt64Value & right.UInt64Value,

            // ── Bitwise OR ─────────────────────────────────────────────────
            BinaryOperatorKind.BoolOr => left.BooleanValue | right.BooleanValue,
            BinaryOperatorKind.IntOr or BinaryOperatorKind.NIntOr
                => left.Int32Value | right.Int32Value,
            BinaryOperatorKind.UIntOr or BinaryOperatorKind.NUIntOr
                => left.UInt32Value | right.UInt32Value,
            BinaryOperatorKind.LongOr => left.Int64Value | right.Int64Value,
            BinaryOperatorKind.ULongOr => left.UInt64Value | right.UInt64Value,

            // ── Bitwise XOR ────────────────────────────────────────────────
            BinaryOperatorKind.BoolXor => left.BooleanValue ^ right.BooleanValue,
            BinaryOperatorKind.IntXor or BinaryOperatorKind.NIntXor
                => left.Int32Value ^ right.Int32Value,
            BinaryOperatorKind.UIntXor or BinaryOperatorKind.NUIntXor
                => left.UInt32Value ^ right.UInt32Value,
            BinaryOperatorKind.LongXor => left.Int64Value ^ right.Int64Value,
            BinaryOperatorKind.ULongXor => left.UInt64Value ^ right.UInt64Value,

            // ── Logical short-circuit (after stripping Logical flag) ────────
            BinaryOperatorKind.LogicalBoolAnd => left.BooleanValue && right.BooleanValue,
            BinaryOperatorKind.LogicalBoolOr => left.BooleanValue || right.BooleanValue,

            // ── Equality / Inequality ──────────────────────────────────────
            BinaryOperatorKind.BoolEqual => left.BooleanValue == right.BooleanValue,
            BinaryOperatorKind.IntEqual or BinaryOperatorKind.NIntEqual
                => left.Int32Value == right.Int32Value,
            BinaryOperatorKind.UIntEqual or BinaryOperatorKind.NUIntEqual
                => left.UInt32Value == right.UInt32Value,
            BinaryOperatorKind.LongEqual => left.Int64Value == right.Int64Value,
            BinaryOperatorKind.ULongEqual => left.UInt64Value == right.UInt64Value,
            BinaryOperatorKind.FloatEqual => Math.Abs(left.SingleValue - right.SingleValue) < 0.001,
            BinaryOperatorKind.DoubleEqual => Math.Abs(left.DoubleValue - right.DoubleValue) < 0.001,
            BinaryOperatorKind.DecimalEqual => left.DecimalValue == right.DecimalValue,
            BinaryOperatorKind.StringEqual => left.StringValue == right.StringValue,

            BinaryOperatorKind.BoolNotEqual => left.BooleanValue != right.BooleanValue,
            BinaryOperatorKind.IntNotEqual or BinaryOperatorKind.NIntNotEqual
                => left.Int32Value != right.Int32Value,
            BinaryOperatorKind.UIntNotEqual or BinaryOperatorKind.NUIntNotEqual
                => left.UInt32Value != right.UInt32Value,
            BinaryOperatorKind.LongNotEqual => left.Int64Value != right.Int64Value,
            BinaryOperatorKind.ULongNotEqual => left.UInt64Value != right.UInt64Value,
            BinaryOperatorKind.FloatNotEqual => Math.Abs(left.SingleValue - right.SingleValue) > 0.001,
            BinaryOperatorKind.DoubleNotEqual => Math.Abs(left.DoubleValue - right.DoubleValue) > 0.001,
            BinaryOperatorKind.DecimalNotEqual => left.DecimalValue != right.DecimalValue,
            BinaryOperatorKind.StringNotEqual => left.StringValue != right.StringValue,

            // ── Less Than ─────────────────────────────────────────────────
            BinaryOperatorKind.IntLessThan or BinaryOperatorKind.NIntLessThan
                => left.Int32Value < right.Int32Value,
            BinaryOperatorKind.UIntLessThan or BinaryOperatorKind.NUIntLessThan
                => left.UInt32Value < right.UInt32Value,
            BinaryOperatorKind.LongLessThan => left.Int64Value < right.Int64Value,
            BinaryOperatorKind.ULongLessThan => left.UInt64Value < right.UInt64Value,
            BinaryOperatorKind.FloatLessThan => left.SingleValue < right.SingleValue,
            BinaryOperatorKind.DoubleLessThan => left.DoubleValue < right.DoubleValue,
            BinaryOperatorKind.DecimalLessThan => left.DecimalValue < right.DecimalValue,

            // ── Less Than Or Equal ─────────────────────────────────────────
            BinaryOperatorKind.IntLessThanOrEqual or BinaryOperatorKind.NIntLessThanOrEqual
                => left.Int32Value <= right.Int32Value,
            BinaryOperatorKind.UIntLessThanOrEqual or BinaryOperatorKind.NUIntLessThanOrEqual
                => left.UInt32Value <= right.UInt32Value,
            BinaryOperatorKind.LongLessThanOrEqual => left.Int64Value <= right.Int64Value,
            BinaryOperatorKind.ULongLessThanOrEqual => left.UInt64Value <= right.UInt64Value,
            BinaryOperatorKind.FloatLessThanOrEqual => left.SingleValue <= right.SingleValue,
            BinaryOperatorKind.DoubleLessThanOrEqual => left.DoubleValue <= right.DoubleValue,
            BinaryOperatorKind.DecimalLessThanOrEqual => left.DecimalValue <= right.DecimalValue,

            // ── Greater Than ───────────────────────────────────────────────
            BinaryOperatorKind.IntGreaterThan or BinaryOperatorKind.NIntGreaterThan
                => left.Int32Value > right.Int32Value,
            BinaryOperatorKind.UIntGreaterThan or BinaryOperatorKind.NUIntGreaterThan
                => left.UInt32Value > right.UInt32Value,
            BinaryOperatorKind.LongGreaterThan => left.Int64Value > right.Int64Value,
            BinaryOperatorKind.ULongGreaterThan => left.UInt64Value > right.UInt64Value,
            BinaryOperatorKind.FloatGreaterThan => left.SingleValue > right.SingleValue,
            BinaryOperatorKind.DoubleGreaterThan => left.DoubleValue > right.DoubleValue,
            BinaryOperatorKind.DecimalGreaterThan => left.DecimalValue > right.DecimalValue,

            // ── Greater Than Or Equal ──────────────────────────────────────
            BinaryOperatorKind.IntGreaterThanOrEqual or BinaryOperatorKind.NIntGreaterThanOrEqual
                => left.Int32Value >= right.Int32Value,
            BinaryOperatorKind.UIntGreaterThanOrEqual or BinaryOperatorKind.NUIntGreaterThanOrEqual
                => left.UInt32Value >= right.UInt32Value,
            BinaryOperatorKind.LongGreaterThanOrEqual => left.Int64Value >= right.Int64Value,
            BinaryOperatorKind.ULongGreaterThanOrEqual => left.UInt64Value >= right.UInt64Value,
            BinaryOperatorKind.FloatGreaterThanOrEqual => left.SingleValue >= right.SingleValue,
            BinaryOperatorKind.DoubleGreaterThanOrEqual => left.DoubleValue >= right.DoubleValue,
            BinaryOperatorKind.DecimalGreaterThanOrEqual => left.DecimalValue >= right.DecimalValue,

            _ => null,
        };
    }
#pragma warning restore CS0162

    // -----------------------------------------------------------------------
    // Unary operator application
    // -----------------------------------------------------------------------

    private static ConstantValue? ApplyUnaryOperator(UnaryOperatorKind kind, ConstantValue operand)
    {
        UnaryOperatorKind baseKind = kind & ~(UnaryOperatorKind.Lifted | UnaryOperatorKind.Checked);

        return baseKind switch
        {
            // Logical negation
            UnaryOperatorKind.BoolLogicalNegation => ConstantValue.Create(!operand.BooleanValue),

            // Bitwise complement
            UnaryOperatorKind.IntBitwiseComplement or UnaryOperatorKind.NIntBitwiseComplement
                => ConstantValue.Create(~operand.Int32Value),
            UnaryOperatorKind.UIntBitwiseComplement or UnaryOperatorKind.NUIntBitwiseComplement
                => ConstantValue.Create(~operand.UInt32Value),
            UnaryOperatorKind.LongBitwiseComplement
                => ConstantValue.Create(~operand.Int64Value),
            UnaryOperatorKind.ULongBitwiseComplement
                => ConstantValue.Create(~operand.UInt64Value),

            // Unary plus (identity)
            UnaryOperatorKind.IntUnaryPlus or UnaryOperatorKind.NIntUnaryPlus
                => ConstantValue.Create(+operand.Int32Value),
            UnaryOperatorKind.UIntUnaryPlus or UnaryOperatorKind.NUIntUnaryPlus
                => ConstantValue.Create(+operand.UInt32Value),
            UnaryOperatorKind.LongUnaryPlus
                => ConstantValue.Create(+operand.Int64Value),
            UnaryOperatorKind.ULongUnaryPlus
                => ConstantValue.Create(+operand.UInt64Value),
            UnaryOperatorKind.FloatUnaryPlus
                => ConstantValue.Create(+operand.SingleValue),
            UnaryOperatorKind.DoubleUnaryPlus
                => ConstantValue.Create(+operand.DoubleValue),
            UnaryOperatorKind.DecimalUnaryPlus
                => ConstantValue.Create(+operand.DecimalValue),

            // Unary minus (negation)
            UnaryOperatorKind.IntUnaryMinus or UnaryOperatorKind.NIntUnaryMinus
                => ConstantValue.Create(unchecked(-operand.Int32Value)),
            UnaryOperatorKind.LongUnaryMinus
                => ConstantValue.Create(unchecked(-operand.Int64Value)),
            UnaryOperatorKind.FloatUnaryMinus
                => ConstantValue.Create(-operand.SingleValue),
            UnaryOperatorKind.DoubleUnaryMinus
                => ConstantValue.Create(-operand.DoubleValue),
            UnaryOperatorKind.DecimalUnaryMinus
                => ConstantValue.Create(-operand.DecimalValue),

            _ => null,
        };
    }

    // -----------------------------------------------------------------------
    // Numeric conversion
    // -----------------------------------------------------------------------

    private static ConstantValue? ConvertToSpecialType(ConstantValue value, SpecialType target)
    {
        try
        {
            return target switch
            {
                SpecialType.System_SByte => ConstantValue.Create(Convert.ToSByte(GetNumericValue(value))),
                SpecialType.System_Byte => ConstantValue.Create(Convert.ToByte(GetNumericValue(value))),
                SpecialType.System_Int16 => ConstantValue.Create(Convert.ToInt16(GetNumericValue(value))),
                SpecialType.System_UInt16 => ConstantValue.Create(Convert.ToUInt16(GetNumericValue(value))),
                SpecialType.System_Int32 => ConstantValue.Create(Convert.ToInt32(GetNumericValue(value))),
                SpecialType.System_UInt32 => ConstantValue.Create(Convert.ToUInt32(GetNumericValue(value))),
                SpecialType.System_Int64 => ConstantValue.Create(Convert.ToInt64(GetNumericValue(value))),
                SpecialType.System_UInt64 => ConstantValue.Create(Convert.ToUInt64(GetNumericValue(value))),
                SpecialType.System_Single => ConstantValue.Create(Convert.ToSingle(GetNumericValue(value))),
                SpecialType.System_Double => ConstantValue.Create(Convert.ToDouble(GetNumericValue(value))),
                SpecialType.System_Decimal => ConstantValue.Create(Convert.ToDecimal(GetNumericValue(value))),
                SpecialType.System_Char => ConstantValue.Create(Convert.ToChar(GetNumericValue(value))),
                SpecialType.System_Boolean => ConstantValue.Create(Convert.ToBoolean(GetNumericValue(value))),
                _ => null,
            };
        }
        catch (OverflowException)
        {
            return ConstantValue.Bad;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    private static object GetNumericValue(ConstantValue value)
    {
        return value.Discriminator switch
        {
            ConstantValueTypeDiscriminator.SByte => (object)value.SByteValue,
            ConstantValueTypeDiscriminator.Byte => value.ByteValue,
            ConstantValueTypeDiscriminator.Int16 => value.Int16Value,
            ConstantValueTypeDiscriminator.UInt16 => value.UInt16Value,
            ConstantValueTypeDiscriminator.Int32 => value.Int32Value,
            ConstantValueTypeDiscriminator.UInt32 => value.UInt32Value,
            ConstantValueTypeDiscriminator.Int64 => value.Int64Value,
            ConstantValueTypeDiscriminator.UInt64 => value.UInt64Value,
            ConstantValueTypeDiscriminator.NInt => (object)value.Int32Value,
            ConstantValueTypeDiscriminator.NUInt => value.UInt32Value,
            ConstantValueTypeDiscriminator.Single => value.SingleValue,
            ConstantValueTypeDiscriminator.Double => value.DoubleValue,
            ConstantValueTypeDiscriminator.Decimal => value.DecimalValue,
            ConstantValueTypeDiscriminator.Char => value.CharValue,
            ConstantValueTypeDiscriminator.Boolean => value.BooleanValue,
            _ => throw new InvalidOperationException($"Unexpected discriminator: {value.Discriminator}"),
        };
    }
}
