// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Tracks the compile-time call stack during consteval interpretation.
/// Each active invocation pushes its <see cref="EvaluationFrame"/>; the frame is
/// popped when the invocation returns. Enforces the recursion depth limit mandated
/// by the consteval specification (rule 18: recursion is permitted but subject to a
/// compile-time recursion depth limit).
/// </summary>
/// <remarks>
/// A single <see cref="EvaluationCallStack"/> is shared for the entire evaluation
/// of one top-level consteval call (across all recursive calls). The frame is pushed
/// before entering a callee and popped on return, preserving correct nesting.
/// </remarks>
internal sealed class EvaluationCallStack
{
    /// <summary>Maximum allowed consteval call depth before reporting an error.</summary>
    public const int MaxRecursionDepth = 512;

    /// <summary>
    /// Maximum number of loop-body iterations allowed in a single loop construct.
    /// Prevents infinite loops from stalling compilation.
    /// </summary>
    public const int MaxLoopIterations = 1_000_000;

    private readonly Stack<EvaluationFrame> _frames = new();

    /// <summary>Current call depth (0 = no active consteval invocation).</summary>
    public int Depth => _frames.Count;

    /// <summary><see langword="true"/> when the next push would exceed <see cref="MaxRecursionDepth"/>.</summary>
    public bool IsDepthExceeded => _frames.Count >= MaxRecursionDepth;

    /// <summary>
    /// The frame at the top of the stack, i.e. the currently executing consteval activation.
    /// Returns <see langword="null"/> when the stack is empty.
    /// </summary>
    public EvaluationFrame? CurrentFrame => _frames.Count > 0 ? _frames.Peek() : null;

    /// <summary>Enters a new consteval activation by pushing <paramref name="frame"/> onto the stack.</summary>
    public void Push(EvaluationFrame frame)
    {
        Debug.Assert(frame is not null);
        _frames.Push(frame);
    }

    /// <summary>Exits the current consteval activation by popping its frame off the stack.</summary>
    public void Pop()
    {
        Debug.Assert(_frames.Count > 0);
        _frames.Pop();
    }
}
