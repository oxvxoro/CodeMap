namespace CodeMap.Core.Models;

public enum EdgeKind
{
    Contains,
    Defines,
    References,
    Calls,
    Imports,
    Inherits,
    Implements,
    Constructs,
    UsesType,
    Overrides,
    ImplementedBy,
    UsedBy,
    UsesCss,
    UsesElement,
    RoutesTo,
    Renders,
    BindsTo,
    HandlesEvent,
    Registers,
    ResolvesTo,
    UsesViewModel
}
