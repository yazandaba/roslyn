// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Symbols;

namespace Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Holds the local state for a single consteval function activation.
/// Each invocation of a consteval function creates an independent frame;
/// frames are never shared between calls, providing isolation guarantees.
/// </summary>
/// <remarks>
/// Parameters may be mutated within the function body (they are value copies; consteval forbids ref/out/in).
/// Locals are mutable within the frame via <see cref="SetLocal"/>.
/// </remarks>
internal sealed class EvaluationFrame
{
    // Parameters are value copies — they may be mutated by the callee body (e.g. n--).
    private readonly Dictionary<ParameterSymbol, ConstantValue> _parameters;

    // Locals are mutated in place during execution (assignments, declarations).
    // The dictionary is private to this frame and never shared.
    private readonly Dictionary<LocalSymbol, ConstantValue> _locals;

    /// <summary>The name of the method this frame was created for.</summary>
    public string FrameName { get; init; }

    private EvaluationFrame(
        Dictionary<ParameterSymbol, ConstantValue> parameters,
        Dictionary<LocalSymbol, ConstantValue> locals)
    {
        _parameters = parameters;
        _locals = locals;
        FrameName = "";
    }

    /// <summary>
    /// Creates a fresh frame for the given method invocation, binding each parameter
    /// to its corresponding argument value.
    /// </summary>
    public static EvaluationFrame Create(
        ImmutableArray<ParameterSymbol> parameters,
        ImmutableArray<ConstantValue> arguments,
        string frameName)
    {
        var paramDict = new Dictionary<ParameterSymbol, ConstantValue>(
            Symbols.SymbolEqualityComparer.ConsiderEverything);

        for (int i = 0; i < parameters.Length; i++)
        {
            paramDict.Add(parameters[i], arguments[i]);
        }

        return new EvaluationFrame(
            paramDict,
            new Dictionary<LocalSymbol, ConstantValue>())
        {
            FrameName = frameName,
        };
    }

    /// <summary>Returns the constant value bound to a parameter, or <see langword="null"/> if not present.</summary>
    public ConstantValue? TryGetParameter(ParameterSymbol parameter)
        => _parameters.TryGetValue(parameter, out var value) ? value : null;

    /// <summary>Stores or updates the constant value for a parameter (e.g. after n-- inside the body).</summary>
    public void SetParameter(ParameterSymbol parameter, ConstantValue value)
        => _parameters[parameter] = value;

    /// <summary>Returns the constant value currently stored for a local, or <see langword="null"/> if uninitialized.</summary>
    public ConstantValue? TryGetLocal(LocalSymbol local)
        => _locals.TryGetValue(local, out var value) ? value : null;

    /// <summary>Stores or updates the constant value for a local.</summary>
    public void SetLocal(LocalSymbol local, ConstantValue value)
        => _locals[local] = value;
}
