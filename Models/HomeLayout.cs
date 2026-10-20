namespace NexLauncher.Models;

public enum HomeLayout { Default, Tiles, List }

public sealed record HomeLayoutChoice(HomeLayout Layout, string Label)
{
    public static HomeLayoutChoice[] Choices { get; } =
    [new(HomeLayout.Default, "Стандартный"), new(HomeLayout.Tiles, "Плитки"), new(HomeLayout.List, "Список")];
}
