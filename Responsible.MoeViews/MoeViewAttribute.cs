namespace Responsible.MoeViews;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MoeViewAttribute(Type viewType) : Attribute
{
    public Type ViewType { get; } = viewType;
}