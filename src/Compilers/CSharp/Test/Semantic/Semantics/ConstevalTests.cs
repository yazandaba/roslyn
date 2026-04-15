// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    /// <summary>
    /// Tests for the <c>consteval</c> modifier – covering:
    ///   1. Invalid placement (constructor, destructor, property, field, operator, indexer)
    ///   2. Invalid modifiers on methods (async, unsafe, partial, extern, virtual, abstract, override, generic)
    ///   3. Invalid signature types (non-primitive params/return, ref/out/in, params, dynamic)
    ///   4. Invalid body constructs (non-consteval call, non-primitive local, non-const field, new,
    ///      try/catch, throw, goto, typeof, sizeof, switch patterns)
    ///   5. Evaluator correctness (arithmetic, loops, switch statement, switch expression, recursion, errors)
    /// </summary>
    public class ConstevalTests : CSharpTestBase
    {
        // ===================================================================
        // 1. INVALID PLACEMENT – consteval on non-method members
        // ===================================================================

        [Fact]
        public void Placement_Constructor_ReportsError()
        {
            // Constructors are not in the allowed set for consteval.
            var source = @"
class C
{
    consteval C() { }
}
";
            var comp = CreateCompilation(source);
            var diags = comp.GetDiagnostics();
            Assert.Contains(diags, d => d.Code == (int)ErrorCode.ERR_BadConstevalItem
                                     || d.Code == (int)ErrorCode.ERR_NoConstevalConstructor);
        }

        [Fact]
        public void Placement_Destructor_ReportsError()
        {
            // Destructors have a specific error: ERR_NoConstevalDestructor.
            var source = @"
class C
{
    consteval ~C() { }
}
";
            CreateCompilation(source).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_NoConstevalDestructor, "C").WithLocation(4, 16));
        }

        [Fact]
        public void Placement_Property_ReportsError()
        {
            var source = @"
class C
{
    consteval int Prop => 5;
}
";
            CreateCompilation(source).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_BadConstevalItem, "Prop").WithLocation(4, 19));
        }

        [Fact]
        public void Placement_Field_ReportsError()
        {
            var source = @"
class C
{
    consteval int _field = 5;
}
";
            // consteval is not in the allowed set for fields.
            var comp = CreateCompilation(source);
            Assert.NotEmpty(comp.GetDiagnostics());
        }

        [Fact]
        public void Placement_Operator_ReportsError()
        {
            var source = @"
class C
{
    public consteval static C operator+(C a, C b) => a;
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_BadConstevalItem);
        }

        [Fact]
        public void Placement_Indexer_ReportsError()
        {
            var source = @"
class C
{
    consteval int this[int i] => i;
}
";
            CreateCompilation(source).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_BadConstevalItem, "this").WithLocation(4, 19));
        }

        // ===================================================================
        // 2. INVALID MODIFIERS ON METHODS
        // ===================================================================

        [Fact]
        public void Modifier_Async_ReportsError()
        {
            var source = @"
class C
{
    consteval static async void M() { }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_ConstevalFunctionCannotBeAsync, "M").WithArguments("M").WithLocation(4, 33));
        }

        [Fact]
        public void Modifier_Unsafe_ReportsError()
        {
            var source = @"
class C
{
    consteval static unsafe int M(int x) => x;
}
";
            CreateCompilation(source, options: TestOptions.UnsafeReleaseDll).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_ConstevalFunctionCannotBeUnsafe, "M").WithArguments("M").WithLocation(4, 33));
        }

        [Fact]
        public void Modifier_Partial_ReportsErrors()
        {
            var source = @"
partial class C
{
    public consteval static partial void M();
}
";
            var comp = CreateCompilation(source);
            // Should get ERR_ConstevalCannotBePartial; may also get missing implementation error
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalCannotBePartial);
        }

        [Fact]
        public void Modifier_Extern_ReportsErrors()
        {
            var source = @"
class C
{
    consteval static extern int M(int x);
}
";
            var comp = CreateCompilation(source);
            // Should get ERR_ConstevalCannotBeExtern; may also get ERR_ConstevalHasNoBody
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalCannotBeExtern);
        }

        [Fact]
        public void Modifier_Virtual_ReportsError()
        {
            var source = @"
class C
{
    public consteval virtual int M(int x) => x;
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalCannotHavePolymorphicModifiers);
        }

        [Fact]
        public void Modifier_Abstract_ReportsErrors()
        {
            var source = @"
abstract class C
{
    public consteval abstract int M(int x);
}
";
            var comp = CreateCompilation(source);
            // ERR_ConstevalCannotHavePolymorphicModifiers and ERR_ConstevalHasNoBody
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalCannotHavePolymorphicModifiers);
        }

        [Fact]
        public void Modifier_Override_ReportsError()
        {
            var source = @"
class Base
{
    public virtual int M(int x) => x;
}
class C : Base
{
    consteval public override int M(int x) => x;
}
";
            CreateCompilation(source).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_ConstevalCannotHavePolymorphicModifiers, "M").WithArguments("M").WithLocation(8, 35));
        }

        [Fact]
        public void Modifier_Generic_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M<T>(int x) => x;
}
";
            CreateCompilation(source).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_ConstevalFunctionCannotBeGeneric, "M").WithArguments("M").WithLocation(4, 26));
        }

        [Fact]
        public void Modifier_NoBody_ReportsError()
        {
            // A consteval method must have a body.
            var source = @"
class C
{
    consteval static extern int M(int x);
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalHasNoBody);
        }

        // ===================================================================
        // 3. INVALID SIGNATURE TYPES
        // ===================================================================

        [Fact]
        public void Signature_NonPrimitiveParameter_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M(object x) => 0;
}
";
            CreateCompilation(source).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_ConstevalParameterMustBePrimitive, "x").WithArguments("x").WithLocation(4, 35));
        }

        [Fact]
        public void Signature_NonPrimitiveReturnType_ReportsError()
        {
            var source = @"
class C
{
    consteval static object M(int x) => x;
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalReturnTypeMustBePrimitive);
        }

        [Fact]
        public void Signature_RefParameter_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M(ref int x) => x;
}
";
            CreateCompilation(source).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_ConstevalParameterCannotBeRefOutIn, "x").WithArguments("x", "Ref").WithLocation(4, 36));
        }

        [Fact]
        public void Signature_OutParameter_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M(out int x) { x = 0; return 0; }
}
";
            CreateCompilation(source).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_ConstevalParameterCannotBeRefOutIn, "x").WithArguments("x", "Out").WithLocation(4, 36));
        }

        [Fact]
        public void Signature_InParameter_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M(in int x) => x;
}
";
            CreateCompilation(source).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_ConstevalParameterCannotBeRefOutIn, "x").WithArguments("x", "In").WithLocation(4, 35));
        }

        [Fact]
        public void Signature_DynamicParameter_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M(dynamic x) => 0;
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalParameterCannotBeDynamic);
        }

        [Fact]
        public void Signature_DynamicReturnType_ReportsError()
        {
            var source = @"
class C
{
    consteval static dynamic M(int x) => x;
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalReturnTypeCannotBeDynamic);
        }

        // ===================================================================
        // 4. INVALID BODY CONSTRUCTS
        // ===================================================================

        [Fact]
        public void Body_CallsNonConstevalMethod_ReportsError()
        {
            var source = @"
class C
{
    static int Regular(int x) => x;
    consteval static int M(int x) => Regular(x);
}
";
            CreateCompilation(source).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_ConstevalCanOnlyCallConsteval, "Regular(x)").WithArguments("Regular").WithLocation(5, 38));
        }

        [Fact]
        public void Body_NonPrimitiveLocal_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M()
    {
        object x = null;
        return 0;
    }
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalLocalMustBePrimitive);
        }

        [Fact]
        public void Body_NonConstFieldAccess_ReportsError()
        {
            var source = @"
class C
{
    static int _val = 5;
    consteval static int M() => _val;
}
";
            CreateCompilation(source).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_ConstevalCanOnlyAccessConstFields, "_val").WithArguments("_val").WithLocation(5, 33));
        }

        [Fact]
        public void Body_ConstFieldAccess_Valid()
        {
            var source = @"
class C
{
    const int Factor = 10;
    consteval static int M(int x) => x * Factor;
    void Use() { _ = M(3); }
}
";
            CreateCompilation(source).VerifyDiagnostics();
        }

        [Fact]
        public void Body_NewExpression_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M()
    {
        var x = new object();
        return 0;
    }
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalCannotUseNewExpression);
        }

        [Fact]
        public void Body_TryCatch_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M()
    {
        try { return 1; }
        catch { return 0; }
    }
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalCannotUseTryCatchFinally);
        }

        [Fact]
        public void Body_Throw_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M()
    {
        throw null;
    }
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalCannotUseThrow);
        }

        [Fact]
        public void Body_Goto_ReportsUnsupportedControlFlow()
        {
            var source = @"
class C
{
    consteval static int M()
    {
        goto done;
        done:
        return 0;
    }
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalUnsupportedControlFlow);
        }

        [Fact]
        public void Body_TypeOf_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M(int x)
    {
        _ = typeof(int);
        return x;
    }
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalCannotUseTypeOf);
        }

        [Fact]
        public void Body_SizeOf_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M(int x)
    {
        _ = sizeof(int);
        return x;
    }
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalCannotUseSizeOf);
        }

        [Fact]
        public void Body_UserDefinedOperator_ReportsError()
        {
            var source = @"
struct Vec { public static Vec operator+(Vec a, Vec b) => default; }
class C
{
    consteval static int M(int x)
    {
        Vec v = default;
        Vec w = v + v;
        return x;
    }
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalCannotUseUserDefinedOperator);
        }

        // ===================================================================
        // 5. SWITCH PATTERN RESTRICTIONS
        // ===================================================================

        [Fact]
        public void Switch_Statement_ConstantPattern_Valid()
        {
            var source = @"
class C
{
    consteval static string Classify(int n)
    {
        switch (n)
        {
            case 1:  return ""one"";
            case 2:  return ""two"";
            default: return ""other"";
        }
    }
    const string S1 = Classify(1);
    const string S2 = Classify(2);
    const string S3 = Classify(99);
}
";
            CreateCompilation(source).VerifyDiagnostics();
        }

        [Fact]
        public void Switch_Statement_WhenClause_Valid()
        {
            var source = @"
class C
{
    consteval static int Tag(int n)
    {
        switch (n)
        {
            case 5 when n > 3: return 100;
            case 5:            return 50;
            default:           return 0;
        }
    }
    const int X = Tag(5);
}
";
            CreateCompilation(source).VerifyDiagnostics();
        }

        [Fact]
        public void Switch_Statement_RelationalPattern_ReportsError()
        {
            var source = @"
class C
{
    consteval static string M(int n)
    {
        switch (n)
        {
            case > 0: return ""pos"";
            default:  return ""other"";
        }
    }
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalSwitchPatternNotAllowed);
        }

        [Fact]
        public void Switch_Statement_DeclarationPattern_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M(int n)
    {
        switch (n)
        {
            case int x: return x;
            default:    return 0;
        }
    }
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalSwitchPatternNotAllowed);
        }

        [Fact]
        public void Switch_Expression_ConstantPattern_Valid()
        {
            var source = @"
class C
{
    consteval static int Map(int x) => x switch
    {
        1 => 100,
        2 => 200,
        _ => -1,
    };
    const int V1 = Map(1);
    const int V2 = Map(2);
    const int V3 = Map(9);
}
";
            CreateCompilation(source).VerifyDiagnostics();
        }

        [Fact]
        public void Switch_Expression_WhenClause_Valid()
        {
            var source = @"
class C
{
    consteval static string Describe(int n) => n switch
    {
        5 when n > 3 => ""big five"",
        5            => ""small five"",
        _            => ""other"",
    };
    const string S = Describe(5);
}
";
            CreateCompilation(source).VerifyDiagnostics();
        }

        [Fact]
        public void Switch_Expression_RelationalPattern_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M(int n) => n switch
    {
        > 0 => 1,
        _   => -1,
    };
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalSwitchPatternNotAllowed);
        }

        [Fact]
        public void Switch_Expression_PropertyPattern_ReportsError()
        {
            var source = @"
class C
{
    consteval static int M(int n) => n switch
    {
        int i when i > 0 => 1,
        _                => 0,
    };
}
";
            var comp = CreateCompilation(source);
            Assert.Contains(comp.GetDiagnostics(),
                d => d.Code == (int)ErrorCode.ERR_ConstevalSwitchPatternNotAllowed);
        }

        // ===================================================================
        // 6. VALID CONSTEVAL – evaluator correctness
        // ===================================================================

        [Fact]
        public void Evaluator_SimpleArithmetic()
        {
            var source = @"
class C
{
    consteval static int Add(int a, int b) => a + b;
    void M() { _ = Add(10, 20); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, 30);
        }

        [Fact]
        public void Evaluator_LocalVariables()
        {
            var source = @"
class C
{
    consteval static int Compute(int x)
    {
        int a = x * 2;
        int b = a + 10;
        return b;
    }
    void M() { _ = Compute(5); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, 20);
        }

        [Fact]
        public void Evaluator_Fibonacci_Recursive()
        {
            var source = @"
class C
{
    consteval static int Fib(int n) => n <= 1 ? n : Fib(n - 1) + Fib(n - 2);
    void M() { _ = Fib(10); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, 55);
        }

        [Fact]
        public void Evaluator_ForLoop_Sum()
        {
            var source = @"
class C
{
    consteval static int Sum(int n)
    {
        int s = 0;
        for (int i = 1; i <= n; i++)
            s += i;
        return s;
    }
    void M() { _ = Sum(5); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, 15);
        }

        [Fact]
        public void Evaluator_WhileLoop()
        {
            var source = @"
class C
{
    consteval static int Countdown(int n)
    {
        int result = 0;
        while (n > 0) { result += n; n--; }
        return result;
    }
    void M() { _ = Countdown(4); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, 10);
        }

        [Fact]
        public void Evaluator_SwitchStatement_Value()
        {
            var source = @"
class C
{
    consteval static int Scale(int x)
    {
        switch (x)
        {
            case 0: return 0;
            case 1: return 10;
            case 2: return 20;
            default: return -1;
        }
    }
    void M() { _ = Scale(2); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, 20);
        }

        [Fact]
        public void Evaluator_SwitchStatement_Default()
        {
            var source = @"
class C
{
    consteval static int Scale(int x)
    {
        switch (x) { case 1: return 10; default: return -1; }
    }
    void M() { _ = Scale(99); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, -1);
        }

        [Fact]
        public void Evaluator_SwitchStatement_WhenClause_Taken()
        {
            var source = @"
class C
{
    consteval static int Tag(int n)
    {
        switch (n)
        {
            case 5 when n > 3: return 100;
            case 5:            return 50;
            default:           return 0;
        }
    }
    void M() { _ = Tag(5); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, 100);
        }

        [Fact]
        public void Evaluator_SwitchStatement_WhenClause_Skipped()
        {
            var source = @"
class C
{
    consteval static int Tag(int n)
    {
        switch (n)
        {
            case 5 when n > 10: return 100;
            case 5:             return 50;
            default:            return 0;
        }
    }
    void M() { _ = Tag(5); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, 50);
        }

        [Fact]
        public void Evaluator_SwitchExpression_Value()
        {
            var source = @"
class C
{
    consteval static int Map(int x) => x switch { 1 => 10, 2 => 20, _ => 0 };
    void M() { _ = Map(2); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, 20);
        }

        [Fact]
        public void Evaluator_SwitchExpression_Default()
        {
            var source = @"
class C
{
    consteval static int Map(int x) => x switch { 1 => 10, _ => 99 };
    void M() { _ = Map(42); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, 99);
        }

        [Fact]
        public void Evaluator_SwitchExpression_WhenClause_Taken()
        {
            var source = @"
class C
{
    consteval static int M(int n) => n switch { 5 when n > 3 => 100, 5 => 50, _ => 0 };
    void M2() { _ = M(5); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, 100);
        }

        [Fact]
        public void Evaluator_SwitchExpression_WhenClause_Skipped()
        {
            var source = @"
class C
{
    consteval static int M(int n) => n switch { 5 when n > 10 => 100, 5 => 50, _ => 0 };
    void M2() { _ = M(5); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, 50);
        }

        [Fact]
        public void Evaluator_StringConcatenation()
        {
            var source = @"
class C
{
    consteval static string Greet(string name) => ""Hello, "" + name + ""!"";
    void M() { _ = Greet(""World""); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, "Hello, World!");
        }

        [Fact]
        public void Evaluator_RecursionDepthExceeded_ReportsError()
        {
            var source = @"
class C
{
    consteval static int Inf(int n) => Inf(n + 1);
    const int X = Inf(0);
}
";
            CreateCompilation(source).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_ConstevalRecursionDepthExceeded, "Inf(0)")
                    .WithArguments("Inf", "512"));
        }

        [Fact]
        public void Evaluator_DivisionByZero_ReportsError()
        {
            var source = @"
class C
{
    consteval static int Div(int a, int b) => a / b;
    const int X = Div(10, 0);
}
";
            CreateCompilation(source).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_ConstevalDivisionByZero, "Div(10, 0)")
                    .WithArguments("Div"));
        }

        [Fact]
        public void Evaluator_NonConstantArgument_NoError_NoConstantValue()
        {
            var source = @"
class C
{
    consteval static int Add(int a, int b) => a + b;
    static void M(int x) { _ = Add(x, 5); }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var invocation = tree.GetRoot().DescendantNodes()
                .OfType<InvocationExpressionSyntax>().First();
            var value = model.GetConstantValue(invocation);
            Assert.False(value.HasValue); // non-constant arg → no compile-time constant
        }

        [Fact]
        public void Evaluator_LocalFunction_Valid()
        {
            var source = @"
class C
{
    static void M()
    {
        consteval static int Square(int x) => x * x;
        _ = Square(7);
    }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertConstantValue(comp, 49);
        }

        [Fact]
        public void Evaluator_ConstField_UsableAsConst()
        {
            // A consteval result used as a const field initializer must produce a constant value.
            var source = @"
class C
{
    consteval static int Mul(int a, int b) => a * b;
    const int Product = Mul(6, 7);
}
";
            CreateCompilation(source).VerifyDiagnostics();
        }

        // ===================================================================
        // 7. EMISSION – consteval methods must not appear in the output assembly
        // ===================================================================

        [Fact]
        public void Emission_ConstevalMethod_IsAbsentFromEmittedType()
        {
            // The consteval method "Add" should not be present as a member of the
            // emitted type; it is a compile-time-only construct.
            var source = @"
class C
{
    public consteval static int Add(int a, int b) => a + b;
    void Use() { _ = Add(3, 4); }
}
";
            CompileAndVerify(source, symbolValidator: module =>
            {
                var typeC = module.GlobalNamespace.GetTypeMembers("C").Single();
                var constevalMembers = typeC.GetMembers("Add");
                Assert.Empty(constevalMembers);
            });
        }

        [Fact]
        public void Emission_RegularMethod_IsPresentInEmittedType()
        {
            // Control: a regular (non-consteval) method IS emitted.
            var source = @"

public class C
{
     
    public static int Add(int a, int b) => a + b;
     
    public static int Sub(int a, int b) => a - b;
     
    public static int Main(string[] args)
    {
        var a = Add(3, args.Length);
        var b = Sub(10, args.Length);
        return a + b;
    }
}
";
            CompileAndVerify(source, symbolValidator: module =>
            {
                var typeC = module.GlobalNamespace.GetTypeMembers("C").Single();
                //var members = typeC.GetMethod("Add");
                var add = typeC.GetMembers("Add").FirstOrDefault();
                var sub = typeC.GetMembers("Sub").FirstOrDefault();
                Assert.NotNull(add);
                Assert.NotNull(sub);
            });
        }

        [Fact]
        public void Emission_ConstevalCallSite_FoldsToConstant()
        {
            // At the call site the consteval invocation is replaced by its constant
            // result, so no call instruction targeting the consteval method appears in IL.
            var source = @"
class C
{
    consteval static int Square(int x) => x * x;
    public static int Get() => Square(9);
}
";
            var verifier = CompileAndVerify(source);
            // The IL for Get() should contain ldc.i4 81 (9*9) and must NOT call Square.
            verifier.VerifyIL("C.Get", @"
{
  // Code size        3 (0x3)
  .maxstack  1
  IL_0000:  ldc.i4.s   81
  IL_0002:  ret
}");
        }

        [Fact]
        public void Emission_MultipleConstevalMethods_NoneEmitted()
        {
            // None of three consteval methods should appear in the emitted assembly.
            var source = @"
class Math
{
    public consteval static int Add(int a, int b) => a + b;
    public consteval static int Mul(int a, int b) => a * b;
    public consteval static int Sub(int a, int b) => a - b;
    public void Use() { _ = Add(1,2); _ = Mul(3,4); _ = Sub(5,6); }
}
";
            CompileAndVerify(source, symbolValidator: module =>
            {
                var typeMath = module.GlobalNamespace.GetTypeMembers("Math").Single();
                Assert.Empty(typeMath.GetMembers("Add"));
                Assert.Empty(typeMath.GetMembers("Mul"));
                Assert.Empty(typeMath.GetMembers("Sub"));
            });
        }

        // ===================================================================
        // 8. CONST LOCALS – consteval functions as initializers
        // ===================================================================

        [Fact]
        public void ConstLocal_Int_InitializedFromConsteval()
        {
            // A const local can be initialized from a consteval call whose arguments
            // are compile-time constants.
            var source = @"
class C
{
    consteval static int Add(int a, int b) => a + b;
    static void M()
    {
        const int x = Add(10, 20);
        _ = x;
    }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertLocalConstantValue(comp, "x", 30);
        }

        [Fact]
        public void ConstLocal_String_InitializedFromConsteval()
        {
            // A const string local can be initialized from a consteval call.
            var source = @"
class C
{
    consteval static string Greet(string name) => ""Hello, "" + name + ""!"";
    static void M()
    {
        const string greeting = Greet(""World"");
        _ = greeting;
    }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertLocalConstantValue(comp, "greeting", "Hello, World!");
        }

        [Fact]
        public void ConstLocal_UsedInAnotherConstLocal()
        {
            // A const local produced from consteval can feed into a second const local.
            var source = @"
class C
{
    consteval static int Fib(int n) => n <= 1 ? n : Fib(n - 1) + Fib(n - 2);
    static void M()
    {
        const int fib7 = Fib(7);
        const int doubled = fib7 * 2;
        _ = doubled;
    }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertLocalConstantValue(comp, "fib7", 13);
            AssertLocalConstantValue(comp, "doubled", 26);
        }

        [Fact]
        public void ConstLocal_UsedInCaseLabel()
        {
            // A const local initialised from consteval may appear in a switch case label.
            var source = @"
class C
{
    consteval static int Scale(int x) => x * 10;
    static string M(int n)
    {
        const int Threshold = Scale(5);
        switch (n)
        {
            case Threshold: return ""fifty"";
            default:        return ""other"";
        }
    }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertLocalConstantValue(comp, "Threshold", 50);
        }

        [Fact]
        public void ConstLocal_UsedAsDefaultParameterValue()
        {
            // A const local from consteval can supply a default parameter value.
            var source = @"
class C
{
    consteval static int Mul(int a, int b) => a * b;
    static void Outer()
    {
        const int DefaultFactor = Mul(6, 7);
        Inner(DefaultFactor);
    }
    static void Inner(int v) { }
}
";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
            AssertLocalConstantValue(comp, "DefaultFactor", 42);
        }

        [Fact]
        public void ConstLocal_NonConstantArgument_CannotInitializeConst()
        {
            // When the argument to a consteval call is not a compile-time constant,
            // the result is not a constant and therefore cannot initialize a const local.
            var source = @"
class C
{
    consteval static int Add(int a, int b) => a + b;
    static void M(int x)
    {
        const int bad = Add(x, 5);
        _ = bad;
    }
}
";
            var comp = CreateCompilation(source);
            // Expect an error because the consteval result is not a constant (x is not const).
            Assert.NotEmpty(comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        }

        [Fact]
        public void ConstLocal_MultipleTypes_AllPrimitive()
        {
            // const locals of bool, char, long, double, and float are all valid.
            var source = @"
class C
{
    consteval static bool  IsPositive(int n) => n > 0;
    consteval static int   Negate(int n) => -n;
    consteval static long  LongAdd(long a, long b) => a + b;
    consteval static double DblMul(double a, double b) => a * b;
    static void M()
    {
        const bool  flag  = IsPositive(5);
        const int   neg   = Negate(3);
        const long  big   = LongAdd(100L, 200L);
        const double prod = DblMul(1.5, 2.0);
        _ = flag; _ = neg; _ = big; _ = prod;
    }
}
";
            CreateCompilation(source).VerifyDiagnostics();
        }

        // ===================================================================
        // Helper
        // ===================================================================

        /// <summary>
        /// Gets the constant value of the first invocation expression found in the compilation
        /// and asserts it equals <paramref name="expected"/>.
        /// </summary>
        private static void AssertConstantValue(CSharpCompilation comp, object expected)
        {
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var invocation = tree.GetRoot().DescendantNodes()
                .OfType<InvocationExpressionSyntax>().Last();
            var value = model.GetConstantValue(invocation);
            Assert.True(value.HasValue, $"Expected constant value {expected} but got no constant.");
            Assert.Equal(expected, value.Value);
        }

        /// <summary>
        /// Finds the const local named <paramref name="localName"/> anywhere in the compilation
        /// and asserts its compile-time constant value equals <paramref name="expected"/>.
        /// </summary>
        private static void AssertLocalConstantValue(CSharpCompilation comp, string localName, object expected)
        {
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var declarator = tree.GetRoot().DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .First(v => v.Identifier.Text == localName);
            var symbol = (ILocalSymbol)model.GetDeclaredSymbol(declarator)!;
            Assert.True(symbol.HasConstantValue, $"Expected '{localName}' to have a constant value.");
            Assert.Equal(expected, symbol.ConstantValue);
        }
    }
}
