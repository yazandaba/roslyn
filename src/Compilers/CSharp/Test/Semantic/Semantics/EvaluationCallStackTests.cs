// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    public class EvaluationCallStackTests
    {
        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static EvaluationFrame MakeFrame(string name)
            => EvaluationFrame.Create(
                ImmutableArray<ParameterSymbol>.Empty,
                ImmutableArray<ConstantValue>.Empty,
                name);

        // -----------------------------------------------------------------------
        // Initial state
        // -----------------------------------------------------------------------

        [Fact]
        public void InitialState_DepthIsZero()
        {
            var stack = new EvaluationCallStack();
            Assert.Equal(0, stack.Depth);
        }

        [Fact]
        public void InitialState_CurrentFrameIsNull()
        {
            var stack = new EvaluationCallStack();
            Assert.Null(stack.CurrentFrame);
        }

        [Fact]
        public void InitialState_IsDepthExceededIsFalse()
        {
            var stack = new EvaluationCallStack();
            Assert.False(stack.IsDepthExceeded);
        }

        // -----------------------------------------------------------------------
        // Push / Pop
        // -----------------------------------------------------------------------

        [Fact]
        public void Push_IncrementsDepth()
        {
            var stack = new EvaluationCallStack();
            stack.Push(MakeFrame("A"));
            Assert.Equal(1, stack.Depth);
        }

        [Fact]
        public void Pop_DecrementsDepth()
        {
            var stack = new EvaluationCallStack();
            stack.Push(MakeFrame("A"));
            stack.Pop();
            Assert.Equal(0, stack.Depth);
        }

        [Fact]
        public void PushPop_ReturnsToZero()
        {
            var stack = new EvaluationCallStack();
            stack.Push(MakeFrame("A"));
            stack.Push(MakeFrame("B"));
            stack.Pop();
            stack.Pop();
            Assert.Equal(0, stack.Depth);
        }

        // -----------------------------------------------------------------------
        // CurrentFrame
        // -----------------------------------------------------------------------

        [Fact]
        public void CurrentFrame_AfterOnePush_IsTheFrame()
        {
            var stack = new EvaluationCallStack();
            var frame = MakeFrame("Foo");
            stack.Push(frame);
            Assert.Same(frame, stack.CurrentFrame);
        }

        [Fact]
        public void CurrentFrame_AfterTwoPushes_IsLatestFrame()
        {
            var stack = new EvaluationCallStack();
            var outer = MakeFrame("Outer");
            var inner = MakeFrame("Inner");
            stack.Push(outer);
            stack.Push(inner);
            Assert.Same(inner, stack.CurrentFrame);
        }

        [Fact]
        public void CurrentFrame_AfterPop_RestoresPreviousFrame()
        {
            var stack = new EvaluationCallStack();
            var outer = MakeFrame("Outer");
            var inner = MakeFrame("Inner");
            stack.Push(outer);
            stack.Push(inner);
            stack.Pop();
            Assert.Same(outer, stack.CurrentFrame);
        }

        [Fact]
        public void CurrentFrame_AfterAllPopped_IsNull()
        {
            var stack = new EvaluationCallStack();
            stack.Push(MakeFrame("A"));
            stack.Pop();
            Assert.Null(stack.CurrentFrame);
        }

        [Fact]
        public void CurrentFrame_FrameName_MatchesPushedMethod()
        {
            var stack = new EvaluationCallStack();
            stack.Push(MakeFrame("MyMethod"));
            Assert.Equal("MyMethod", stack.CurrentFrame!.FrameName);
        }

        // -----------------------------------------------------------------------
        // Depth limit
        // -----------------------------------------------------------------------

        [Fact]
        public void IsDepthExceeded_FalseBeforeLimit()
        {
            var stack = new EvaluationCallStack();
            for (int i = 0; i < EvaluationCallStack.MaxRecursionDepth - 1; i++)
                stack.Push(MakeFrame($"f{i}"));
            Assert.False(stack.IsDepthExceeded);
        }

        [Fact]
        public void IsDepthExceeded_TrueAtLimit()
        {
            var stack = new EvaluationCallStack();
            for (int i = 0; i < EvaluationCallStack.MaxRecursionDepth; i++)
                stack.Push(MakeFrame($"f{i}"));
            Assert.True(stack.IsDepthExceeded);
        }

        [Fact]
        public void IsDepthExceeded_FalseAfterPopBelowLimit()
        {
            var stack = new EvaluationCallStack();
            for (int i = 0; i < EvaluationCallStack.MaxRecursionDepth; i++)
                stack.Push(MakeFrame($"f{i}"));
            stack.Pop();
            Assert.False(stack.IsDepthExceeded);
        }

        // -----------------------------------------------------------------------
        // LIFO ordering of FrameName
        // -----------------------------------------------------------------------

        [Fact]
        public void FrameNames_FollowLIFOOrder()
        {
            var stack = new EvaluationCallStack();
            stack.Push(MakeFrame("A"));
            stack.Push(MakeFrame("B"));
            stack.Push(MakeFrame("C"));

            Assert.Equal("C", stack.CurrentFrame!.FrameName);
            stack.Pop();
            Assert.Equal("B", stack.CurrentFrame!.FrameName);
            stack.Pop();
            Assert.Equal("A", stack.CurrentFrame!.FrameName);
            stack.Pop();
            Assert.Null(stack.CurrentFrame);
        }
    }
}
