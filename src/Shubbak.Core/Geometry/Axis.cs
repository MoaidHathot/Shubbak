namespace Shubbak.Core.Geometry;

/// <summary>
/// The two axes a container can lay its children out along.
/// </summary>
public enum Axis
{
    /// <summary>Children are placed left-to-right; the main axis is X.</summary>
    Horizontal,

    /// <summary>Children are placed top-to-bottom; the main axis is Y.</summary>
    Vertical,
}

/// <summary>
/// A cardinal direction, used for focus movement, window movement, and resizing.
/// </summary>
public enum Direction
{
    Left,
    Right,
    Up,
    Down,
}

public static class DirectionExtensions
{
    /// <summary>The axis a direction travels along.</summary>
    public static Axis Axis(this Direction direction) => direction switch
    {
        Direction.Left or Direction.Right => Geometry.Axis.Horizontal,
        _ => Geometry.Axis.Vertical,
    };

    /// <summary>
    /// True when the direction points towards increasing coordinates (right/down),
    /// i.e. towards the end of a container's child list.
    /// </summary>
    public static bool IsForward(this Direction direction) =>
        direction is Direction.Right or Direction.Down;

    public static Direction Opposite(this Direction direction) => direction switch
    {
        Direction.Left => Direction.Right,
        Direction.Right => Direction.Left,
        Direction.Up => Direction.Down,
        _ => Direction.Up,
    };

    /// <summary>The axis perpendicular to this one.</summary>
    public static Axis Cross(this Axis axis) =>
        axis == Geometry.Axis.Horizontal ? Geometry.Axis.Vertical : Geometry.Axis.Horizontal;
}
