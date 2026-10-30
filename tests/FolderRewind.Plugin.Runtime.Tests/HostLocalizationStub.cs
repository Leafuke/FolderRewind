namespace FolderRewind.Services;

// The linked Host capture owner reports resource keys; Runtime tests have no WinUI resource loader.
internal static class I18n
{
    internal static string GetString(string key) => key;
}
