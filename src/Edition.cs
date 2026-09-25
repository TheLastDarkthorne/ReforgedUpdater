using System;
using System.Collections.Generic;
using System.Linq;

namespace ReforgedUpdater
{
    /// <summary>
    /// One Project Reforged patch library. WotLK has a single catalog; vanilla has one per
    /// server, because a few modules (A, I, M on Kronos) are built for that server's client.
    /// </summary>
    internal sealed class Edition
    {
        public string Name { get; }          // what is stored and typed: "wotlk", "kronos", "turtle"
        public string Title { get; }
        public string CatalogUrl { get; }
        public int ClientMajor { get; }      // the Wow.exe major version this edition runs on
        private readonly string[] _aliases;

        private Edition(string name, string title, string catalogUrl, int clientMajor, params string[] aliases)
        {
            Name = name;
            Title = title;
            CatalogUrl = catalogUrl;
            ClientMajor = clientMajor;
            _aliases = aliases;
        }

        public static readonly Edition Wotlk = new Edition(
            "wotlk", "WotLK 3.3.5", "https://projectreforged.github.io/wotlk/downloads/", 3,
            "wrath", "3.3.5", "3.3.5a", "335");

        public static readonly Edition Kronos = new Edition(
            "kronos", "Vanilla 1.12 - Kronos", "https://projectreforged.github.io/vanilla/downloads/kronos/", 1,
            "vanilla-kronos");

        public static readonly Edition Turtle = new Edition(
            "turtle", "Vanilla 1.12 - Turtle WoW", "https://projectreforged.github.io/vanilla/downloads/turtle/", 1,
            "vanilla-turtle", "turtlewow", "turtle-wow");

        public static readonly IReadOnlyList<Edition> All = new[] { Wotlk, Kronos, Turtle };

        public static string Names => string.Join(", ", All.Select(e => e.Name));

        /// <summary>Accepts the name or any alias, case-insensitively. Returns null when unknown.</summary>
        public static Edition Find(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string key = value.Trim();
            return All.FirstOrDefault(e => e.Name.Equals(key, StringComparison.OrdinalIgnoreCase)
                                        || e._aliases.Any(a => a.Equals(key, StringComparison.OrdinalIgnoreCase)));
        }

        public static Edition Parse(string value)
        {
            if (value != null && value.Trim().Equals("vanilla", StringComparison.OrdinalIgnoreCase))
                throw new UpdaterException("Vanilla has a separate patch set per server. Use --edition kronos "
                                           + "or --edition turtle (Turtle WoW's custom client needs its own build).");

            return Find(value) ?? throw new UpdaterException(
                "Unknown edition \"" + value + "\". Choose one of: " + Names + ".");
        }

        public override string ToString() => Name;
    }
}
