using System;
using System.Collections.Generic;

namespace FolderRewind.Services
{
    public static class IconCatalog
    {
        // Segoe MDL2 Assets glyphs.
        // Keep this list curated (useful + recognizable) and stable (config.json stores the glyph).
        public static readonly IReadOnlyList<string> ConfigIconGlyphs = Array.AsReadOnly(new[]
        {
            "\uE8B7", // Folder
            "\uE838", // Home
            "\uE7C3", // Settings
            "\uE8EF", // Save
            "\uE74D", // Document
            "\uE8A5", // OpenFile
            "\uE7F0", // Edit
            "\uE8C8", // Tag
            "\uE7EE", // FavoriteStar
            "\uE734", // Heart
            "\uE71D", // Mail
            "\uE8BD", // Calendar
            "\uE823", // Shop
            "\uE8D2", // Repair
            "\uE774", // Plug
            "\uE7C1", // World
            "\uE80F", // People
            "\uE716", // Camera
            "\uE8A7", // OpenInNewWindow
            "\uE712", // More
            "\uE8CB", // FolderOpen
            "\uE7B8", // Download
            "\uE896", // Upload
            "\uE73E", // Clock
            "\uE7C5", // History
            "\uE768", // Random
            "\uE7B7", // Add
            "\uE74A", // Delete
            "\uE8FB", // Accept
            "\uEA39", // Warning
            "\uE814", // Link
            "\uE8C7", // ReportHacked
            "\uE9CE", // Shield
            "\uE9D9", // Info
            "\uE8F2", // Refresh
            "\uE7BA", // Search
            "\uE8A9", // Filter
            "\uE7F7", // Pin
            "\uEB9F", // Game
            "\uE7FC", // Library
            "\uE943", // MapDrive
            "\uE77B", // Note
            "\uEA86", // Toolbox
            "\uE8C4", // FolderShared
            "\uE7A8", // Contact
            "\uE8D7", // Print
            "\uE7E7", // Lightbulb
            "\uE8D4", // Package
        });

        public const string DefaultConfigIconGlyph = "\uE8B7";
    }
}
