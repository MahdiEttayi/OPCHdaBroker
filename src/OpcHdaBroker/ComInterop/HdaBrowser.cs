// ═══════════════════════════════════════════════════════════════════════════
// HDA TAG BROWSER
// ───────────────────────────────────────────────────────────────────────────
// Uses the Technosoftware SDK's built-in ITsCHdaBrowser interface to
// discover tags in KepServerEX Local Historian. No raw COM QI needed.
//
// Tag discovery strategy (in order):
//   1. SDK CreateBrowser → recursive tree walk  (best)
//   2. tags.txt config file                     (reliable fallback)
//   3. POST /api/tags/add                       (manual registration)
//
// Must be called from the COM thread via StaThreadDispatcher.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpcClientSdk;
using OpcClientSdk.Hda;
using Serilog;

namespace OpcHdaBroker.ComInterop
{
    public class HdaBrowser
    {
        private static readonly ILogger Log = Serilog.Log.ForContext<HdaBrowser>();

        private readonly HdaConnection _connection;
        private readonly string _tagsFilePath;

        public HdaBrowser(HdaConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));

            // tags.txt lives next to the exe
            _tagsFilePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "tags.txt");
        }

        /// <summary>
        /// Discover all historian tags using the best available method.
        /// Must be called on the COM thread.
        /// </summary>
        public List<string> DiscoverAllTags()
        {
            var tags = new List<string>();

            // ── Strategy 1: SDK Browser ──────────────────────────────────
            try
            {
                var sdkTags = BrowseViaSdk();
                if (sdkTags.Count > 0)
                {
                    Log.Information("SDK browser discovered {Count} tag(s)", sdkTags.Count);
                    tags.AddRange(sdkTags);
                }
                else
                {
                    Log.Warning("SDK browser returned 0 tags (namespace may be empty at root)");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SDK browse failed: {Message}", ex.Message);
            }

            // ── Strategy 2: TSD .name files (auto-discovery) ─────────────
            try
            {
                var tsdTags = DiscoverFromTsdNameFiles();
                if (tsdTags.Count > 0)
                {
                    int newCount = 0;
                    foreach (var t in tsdTags)
                    {
                        if (!tags.Contains(t, StringComparer.OrdinalIgnoreCase))
                        {
                            tags.Add(t);
                            newCount++;
                        }
                    }
                    if (newCount > 0)
                        Log.Information("Discovered {Count} additional tag(s) from TSD datastore files", newCount);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("TSD auto-discovery failed: {Message}", ex.Message);
            }

            // ── Strategy 3: tags.txt config file ─────────────────────────
            try
            {
                var fileTags = LoadTagsFromFile();
                if (fileTags.Count > 0)
                {
                    int newCount = 0;
                    foreach (var t in fileTags)
                    {
                        if (!tags.Contains(t, StringComparer.OrdinalIgnoreCase))
                        {
                            tags.Add(t);
                            newCount++;
                        }
                    }
                    if (newCount > 0)
                        Log.Information("Loaded {Count} additional tag(s) from configuration file", newCount);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not load tags.txt: {Message}", ex.Message);
            }

            return tags;
        }

        /// <summary>
        /// Browse the historian namespace using the SDK's built-in browser.
        /// This calls CreateBrowser() which wraps IOPCHDA_Browser internally.
        /// </summary>
        private List<string> BrowseViaSdk()
        {
            var server = _connection.Server;
            var tags = new List<string>();

            // CreateBrowser signature: ITsCHdaBrowser CreateBrowser(TsCHdaBrowseFilter[] filters, out OpcResult[] results)
            OpcResult[] filterResults;
            var browser = server.CreateBrowser(null, out filterResults);

            if (browser == null)
            {
                Log.Warning("CreateBrowser returned null");
                return tags;
            }

            try
            {
                Log.Debug("SDK browser created successfully ({Type})", browser.GetType().Name);

                // Browse from root (null = root of namespace)
                BrowseRecursive(browser, null, tags, 0, maxDepth: 10, maxTags: 50000);

                Log.Information("SDK browse complete: {Count} leaf tag(s) found", tags.Count);
            }
            finally
            {
                browser.Dispose();
            }

            return tags;
        }

        /// <summary>
        /// Recursively walk the OPC HDA namespace tree.
        /// 
        /// IMPORTANT: KepServerEX may report HasChildren=False on branch nodes.
        /// We always attempt to browse into non-item elements to find all tags.
        /// </summary>
        private void BrowseRecursive(
            ITsCHdaBrowser browser, OpcItem parentItem,
            List<string> tags, int depth, int maxDepth, int maxTags)
        {
            if (depth > maxDepth || tags.Count >= maxTags)
                return;

            string indent = new string(' ', depth * 2);
            TsCHdaBrowseElement[] elements;

            try
            {
                elements = browser.Browse(parentItem);
            }
            catch (Exception ex)
            {
                Log.Debug("{Indent}Browse at depth {Depth} failed: {Msg}", indent, depth, ex.Message);
                return;
            }

            if (elements == null || elements.Length == 0)
            {
                Log.Debug("{Indent}No elements at depth {Depth} (parent={Parent})",
                    indent, depth, parentItem?.ItemName ?? parentItem?.ItemPath ?? "root");
                return;
            }

            Log.Debug("{Indent}Found {Count} elements at depth {Depth}", indent, elements.Length, depth);

            foreach (var elem in elements)
            {
                if (tags.Count >= maxTags) break;

                string itemPath = elem.ItemPath;
                string itemName = elem.ItemName;
                string name = elem.Name;
                bool isItem = elem.IsItem;
                bool hasCh = elem.HasChildren;

                // Use Name as the identifier when ItemName is null
                string browseName = !string.IsNullOrEmpty(itemName) ? itemName : name;

                Log.Information("{Indent}  [{Depth}] {Name} → ItemName={ItemName}, IsItem={IsItem}, HasCh={HasCh}",
                    indent, depth, name, itemName ?? "(null)", isItem, hasCh);

                // Leaf nodes are actual historian tags
                if (isItem)
                {
                    tags.Add(browseName);
                    Log.Information("{Indent}    → TAG: {TagId}", indent, browseName);
                }

                // Always try to recurse into non-item elements (branches).
                // KepServerEX doesn't always report HasChildren accurately.
                if (!isItem)
                {
                    try
                    {
                        var childItem = new OpcItem(browseName);
                        if (!string.IsNullOrEmpty(itemPath))
                            childItem.ItemPath = itemPath;

                        BrowseRecursive(browser, childItem, tags, depth + 1, maxDepth, maxTags);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("{Indent}    Recurse into '{Name}' failed: {Msg}", indent, name, ex.Message);
                    }
                }

                // Some elements are both items AND have children (rare but possible)
                if (isItem && hasCh)
                {
                    try
                    {
                        var childItem = new OpcItem(browseName);
                        if (!string.IsNullOrEmpty(itemPath))
                            childItem.ItemPath = itemPath;

                        BrowseRecursive(browser, childItem, tags, depth + 1, maxDepth, maxTags);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("{Indent}    Recurse into item+branch '{Name}' failed: {Msg}", indent, name, ex.Message);
                    }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // TSD DATASTORE AUTO-DISCOVERY
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Discover historian tag paths by reading KepServerEX Local Historian
        /// .name files. These binary files embed tag paths as strings in the
        /// format "Channel.Device.Tag". We scan the file contents to extract
        /// any valid dotted tag paths.
        /// </summary>
        private List<string> DiscoverFromTsdNameFiles()
        {
            var tags = new List<string>();

            // Standard KepServerEX Historical Data path
            string histDir = System.Configuration.ConfigurationManager.AppSettings["Hda.TsdDataPath"]
                ?? @"C:\ProgramData\Kepware\KEPServerEX\V6\Historical Data";

            if (!Directory.Exists(histDir))
            {
                Log.Debug("TSD data directory not found: {Path}", histDir);
                return tags;
            }

            var nameFiles = Directory.GetFiles(histDir, "*.name", SearchOption.AllDirectories);
            Log.Debug("Found {Count} .name file(s) in {Path}", nameFiles.Length, histDir);

            foreach (var nameFile in nameFiles)
            {
                try
                {
                    // Use FileShare.ReadWrite to handle files locked by KepServerEX
                    byte[] bytes;
                    using (var fs = new FileStream(nameFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        bytes = new byte[fs.Length];
                        fs.Read(bytes, 0, bytes.Length);
                    }
                    string content = System.Text.Encoding.UTF8.GetString(bytes);

                    // Extract tag paths: "Channel.Device.Tag" or "Channel.Device.Group.Tag"
                    // Segments can start with letters or digits (e.g., "16 Bit Device")
                    var matches = System.Text.RegularExpressions.Regex.Matches(
                        content,
                        @"[A-Za-z0-9_][A-Za-z0-9_ ]*(?:\.[A-Za-z0-9_][A-Za-z0-9_ ]*){2,}");

                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        string tagPath = match.Value.Trim();
                        if (!string.IsNullOrEmpty(tagPath) &&
                            !tags.Contains(tagPath, StringComparer.OrdinalIgnoreCase))
                        {
                            tags.Add(tagPath);
                            Log.Debug("TSD auto-discovered tag: {Tag}", tagPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("Failed to read .name file {File}: {Msg}", nameFile, ex.Message);
                }
            }

            Log.Information("TSD auto-discovery found {Count} tag(s) across {Files} datastore(s)",
                tags.Count, nameFiles.Length);
            return tags;
        }

        // ══════════════════════════════════════════════════════════════════
        // FILE-BASED TAG MANAGEMENT
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Load tag paths from tags.txt (one per line, # comments).
        /// </summary>
        private List<string> LoadTagsFromFile()
        {
            if (!File.Exists(_tagsFilePath))
                return new List<string>();

            var tags = File.ReadAllLines(_tagsFilePath)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Log.Information("Loaded {Count} tag(s) from {Path}", tags.Count, _tagsFilePath);
            return tags;
        }

        /// <summary>
        /// Persist current tag list to tags.txt.
        /// </summary>
        public void SaveTagsToFile(List<string> tags)
        {
            var lines = new List<string>
            {
                "# OPC HDA Broker — Tag Configuration",
                "# One tag path per line. Lines starting with # are comments.",
                "# Auto-saved by the broker.",
                ""
            };
            lines.AddRange(tags.Distinct(StringComparer.OrdinalIgnoreCase));
            File.WriteAllLines(_tagsFilePath, lines);
            Log.Information("Saved {Count} tag(s) to {Path}", tags.Count, _tagsFilePath);
        }

        /// <summary>
        /// Add new tags to an existing list (deduped).
        /// </summary>
        public void AddTags(List<string> existingTags, List<string> newTags)
        {
            foreach (var tag in newTags)
            {
                string trimmed = tag?.Trim();
                if (!string.IsNullOrEmpty(trimmed) &&
                    !existingTags.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    existingTags.Add(trimmed);
                }
            }
        }
    }
}
