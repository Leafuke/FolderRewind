namespace FolderRewind.Services;

internal static class ThemeSettingPolicy
{
    public const int DefaultIndex = 2;

    public static int Normalize(int? index)
        => index is >= 0 and <= 2 ? index.Value : DefaultIndex;
}
