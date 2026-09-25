using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace ReforgedUpdater
{
    /// <summary>What we recorded about one installed .mpq at the moment we installed it.</summary>
    [DataContract]
    internal sealed class InstalledFile
    {
        [DataMember(Order = 0)] public string Id { get; set; }
        [DataMember(Order = 1)] public string FileName { get; set; }
        [DataMember(Order = 2)] public string Url { get; set; }
        [DataMember(Order = 3)] public string Version { get; set; }
        [DataMember(Order = 4)] public string ETag { get; set; }
        [DataMember(Order = 5)] public string LastModified { get; set; }
        [DataMember(Order = 6)] public long Size { get; set; }
        [DataMember(Order = 7)] public string Sha256 { get; set; }
        [DataMember(Order = 8)] public string InstalledAt { get; set; }

        /// <summary>
        /// Set when the file was adopted but is known not to be the published build. Size and
        /// ETag comparisons cannot express that on their own: an older build can be exactly
        /// the same length, which would otherwise read as up to date.
        /// </summary>
        [DataMember(Order = 9)] public bool ContentDiffers { get; set; }
    }

    [DataContract]
    internal sealed class InstallState
    {
        [DataMember(Order = 0)] public int SchemaVersion { get; set; }
        [DataMember(Order = 1)] public string LastCheck { get; set; }
        [DataMember(Order = 2)] public List<InstalledFile> Files { get; set; }

        /// <summary>Which patch library this client uses ("wotlk", "kronos", "turtle"). Null until known.</summary>
        [DataMember(Order = 3)] public string Edition { get; set; }

        /// <summary>
        /// Where this client's .mpq files go, when not &lt;client&gt;\Data. Kept here rather
        /// than beside the exe so one exe can serve several games without one game's Data
        /// folder leaking into another.
        /// </summary>
        [DataMember(Order = 4)] public string DataPath { get; set; }

        public InstallState()
        {
            SchemaVersion = 1;
            Files = new List<InstalledFile>();
        }

        // The serializer skips constructors, so a file missing a member leaves it null.
        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            if (Files == null) Files = new List<InstalledFile>();
        }

        public InstalledFile Find(string id) =>
            Files.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

        public void Record(InstalledFile file)
        {
            Files.RemoveAll(f => string.Equals(f.Id, file.Id, StringComparison.OrdinalIgnoreCase));
            Files.Add(file);
            Files.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        }

        public void Forget(string id) =>
            Files.RemoveAll(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A game folder registered under a short name, so commands can say --game turtle.</summary>
    [DataContract]
    internal sealed class GameEntry
    {
        [DataMember(Order = 0)] public string Name { get; set; }
        [DataMember(Order = 1)] public string Path { get; set; }
    }

    /// <summary>User settings, stored next to the executable so the tool stays portable.</summary>
    [DataContract]
    internal sealed class Settings
    {
        /// <summary>The game folder used most recently - the default when no game is named.</summary>
        [DataMember(Order = 0)] public string WowPath { get; set; }

        /// <summary>
        /// Legacy: a Data folder applied to whichever game ran. That leaked between games, so
        /// it now lives in each game's state.json; a value found here is migrated and cleared.
        /// </summary>
        [DataMember(Order = 1, EmitDefaultValue = false)] public string DataPath { get; set; }

        /// <summary>
        /// Overrides the edition's downloads page - for a mirror, or a site move. Null means
        /// "use the edition's page". Older settings files stored the WotLK page here as a
        /// default, which is treated the same as null.
        /// </summary>
        [DataMember(Order = 2)] public string CatalogUrl { get; set; }

        [DataMember(Order = 3)] public bool VerifyHash { get; set; }

        [DataMember(Order = 4)] public List<GameEntry> Games { get; set; }

        /// <summary>
        /// The window's colours: "light", "dark", or null to follow Windows. Only the window
        /// reads it; it lives here so both programs keep one settings file.
        /// </summary>
        [DataMember(Order = 5, EmitDefaultValue = false)] public string WindowTheme { get; set; }

        public Settings()
        {
            VerifyHash = true;
            Games = new List<GameEntry>();
        }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            if (Games == null) Games = new List<GameEntry>();
        }

        public GameEntry FindGame(string name) =>
            Games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

        public GameEntry FindGameByPath(string path) =>
            Games.FirstOrDefault(g => string.Equals(
                System.IO.Path.GetFullPath(g.Path).TrimEnd('\\', '/'),
                System.IO.Path.GetFullPath(path).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase));

        public string CatalogOverride =>
            string.IsNullOrWhiteSpace(CatalogUrl) || CatalogUrl.Equals(Catalog.DefaultUrl, StringComparison.OrdinalIgnoreCase)
                ? null
                : CatalogUrl;
    }

    /// <summary>
    /// JSON persistence on top of DataContractJsonSerializer, which ships with the
    /// framework - the tool stays a single dependency-free executable.
    /// </summary>
    internal static class Store
    {
        public static T Load<T>(string path) where T : new()
        {
            if (!File.Exists(path)) return new T();
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    if (stream.Length == 0) return new T();
                    var serializer = new DataContractJsonSerializer(typeof(T));
                    return (T)serializer.ReadObject(stream);
                }
            }
            catch (Exception ex)
            {
                Ui.Warn("Could not read " + Path.GetFileName(path) + " (" + ex.Message + "); starting fresh.");
                Backup(path);
                return new T();
            }
        }

        public static void Save<T>(string path, T value)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            byte[] payload;
            using (var buffer = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                serializer.WriteObject(buffer, value);
                payload = buffer.ToArray();
            }

            // Write to a sibling temp file first: a crash mid-write must not destroy the state.
            string temp = path + ".tmp";
            File.WriteAllText(temp, Prettify(Encoding.UTF8.GetString(payload)), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static void Backup(string path)
        {
            try
            {
                string backup = path + ".bad";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(path, backup);
            }
            catch { /* best effort only */ }
        }

        /// <summary>DataContractJsonSerializer emits one long line; indent it so users can read it.</summary>
        private static string Prettify(string json)
        {
            var output = new StringBuilder(json.Length * 2);
            int depth = 0;
            bool inString = false, escaped = false;

            foreach (char c in json)
            {
                if (inString)
                {
                    output.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        output.Append(c);
                        break;
                    case '{':
                    case '[':
                        output.Append(c).Append('\n').Append(new string(' ', ++depth * 2));
                        break;
                    case '}':
                    case ']':
                        output.Append('\n').Append(new string(' ', --depth * 2)).Append(c);
                        break;
                    case ',':
                        output.Append(c).Append('\n').Append(new string(' ', depth * 2));
                        break;
                    case ':':
                        output.Append(": ");
                        break;
                    default:
                        output.Append(c);
                        break;
                }
            }

            return output.ToString();
        }
    }
}
