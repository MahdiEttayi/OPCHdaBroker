// ═══════════════════════════════════════════════════════════════════════════
// API RESPONSE MODELS
// ───────────────────────────────────────────────────────────────────────────
// Standard envelope for all REST API responses.
// Every response has { data, meta } for consistent client parsing.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;

namespace OpcHdaBroker.Api.Models
{
    /// <summary>
    /// Standard API response envelope.
    /// </summary>
    public class ApiResponse<T>
    {
        public T        Data { get; set; }
        public ApiMeta  Meta { get; set; }
    }

    public class ApiMeta
    {
        public int     Count       { get; set; }
        public long    ExecutionMs { get; set; }
        public string  From        { get; set; }
        public string  To          { get; set; }
        public string  Error       { get; set; }
    }

    /// <summary>
    /// Tag info returned by /api/tags.
    /// </summary>
    public class TagInfoDto
    {
        public string ItemId      { get; set; }
        public string DisplayPath { get; set; }
    }

    /// <summary>
    /// Time-series data point — compact JSON keys for bandwidth.
    /// </summary>
    public class PointDto
    {
        /// <summary>Timestamp (ISO 8601 UTC)</summary>
        public string T { get; set; }

        /// <summary>Value (numeric, boolean, or string)</summary>
        public object V { get; set; }

        /// <summary>Quality string</summary>
        public string Q { get; set; }
    }

    /// <summary>
    /// Read result for a single tag.
    /// </summary>
    public class TagDataDto
    {
        public string          Tag    { get; set; }
        public int             Count  { get; set; }
        public List<PointDto>  Points { get; set; }
        public string          Error  { get; set; }
    }

    /// <summary>
    /// Historian status info from /api/status.
    /// </summary>
    public class StatusDto
    {
        public bool              Connected        { get; set; }
        public string            ServerStatus     { get; set; }
        public string            ServerVersion    { get; set; }
        public string            VendorInfo       { get; set; }
        public int               TagCount         { get; set; }
        public string            BrokerUptime     { get; set; }
        public DateTime          BrokerStartedAt  { get; set; }
        public Dictionary<int, string> SupportedAggregates { get; set; }
    }
}
