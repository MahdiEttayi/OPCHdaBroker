// ═══════════════════════════════════════════════════════════════════════════
// READ CONTROLLER
// ───────────────────────────────────────────────────────────────────────────
// GET /api/read/raw        — Raw historical values
// GET /api/read/latest     — Most recent value per tag
// GET /api/read/processed  — Server-side aggregated values
// GET /api/read/aggregates — List supported aggregate functions
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Http;
using OpcHdaBroker.Api.Models;
using OpcHdaBroker.ComInterop;

namespace OpcHdaBroker.Api.Controllers
{
    [RoutePrefix("api/read")]
    public class ReadController : ApiController
    {
        /// <summary>
        /// Read raw historical values from the historian.
        /// Each API call translates directly to an OPC HDA ReadRaw() COM call.
        ///
        /// Example:
        ///   GET /api/read/raw?tags=PLF_A10.A10.10QT0002&amp;from=2026-04-29T00:00:00Z&amp;to=2026-04-30T00:00:00Z
        /// </summary>
        [HttpGet]
        [Route("raw")]
        public async Task<IHttpActionResult> ReadRaw(
            [FromUri] string   tags,
            [FromUri] DateTime from,
            [FromUri] DateTime to,
            [FromUri] int      maxValues = 10000)
        {
            var sw = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(tags))
                return BadRequest("'tags' parameter is required (comma-separated tag paths)");

            if (maxValues > 100000) maxValues = 100000;

            var tagList = tags.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(t => t.Trim())
                              .ToList();

            try
            {
                var results = await BrokerEngine.ReadRawAsync(tagList, from, to, maxValues);
                var dto = MapToDto(results);

                return Ok(new ApiResponse<List<TagDataDto>>
                {
                    Data = dto,
                    Meta = new ApiMeta
                    {
                        Count       = dto.Sum(d => d.Count),
                        ExecutionMs = sw.ElapsedMilliseconds,
                        From        = from.ToUniversalTime().ToString("o"),
                        To          = to.ToUniversalTime().ToString("o")
                    }
                });
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }

        /// <summary>
        /// Read the most recent value for each specified tag.
        ///
        /// Example:
        ///   GET /api/read/latest?tags=PLF_A10.A10.10QT0002,PLF_A10.A10.10QT0003
        /// </summary>
        [HttpGet]
        [Route("latest")]
        public async Task<IHttpActionResult> ReadLatest(
            [FromUri] string tags,
            [FromUri] int    lookbackMinutes = 60)
        {
            var sw = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(tags))
                return BadRequest("'tags' parameter is required");

            var tagList = tags.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(t => t.Trim())
                              .ToList();

            try
            {
                var results = await BrokerEngine.ReadLatestAsync(tagList, lookbackMinutes);
                var dto = MapToDto(results);

                return Ok(new ApiResponse<List<TagDataDto>>
                {
                    Data = dto,
                    Meta = new ApiMeta
                    {
                        Count       = dto.Count,
                        ExecutionMs = sw.ElapsedMilliseconds
                    }
                });
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }

        /// <summary>
        /// Read server-side aggregated values from the historian.
        /// KepServerEX performs the aggregation on TSD files directly.
        ///
        /// Example:
        ///   GET /api/read/processed?tags=PLF_A10.A10.10QT0002&amp;from=2026-04-29T00:00:00Z
        ///       &amp;to=2026-04-30T00:00:00Z&amp;aggregate=average&amp;intervalSec=3600
        /// </summary>
        [HttpGet]
        [Route("processed")]
        public async Task<IHttpActionResult> ReadProcessed(
            [FromUri] string   tags,
            [FromUri] DateTime from,
            [FromUri] DateTime to,
            [FromUri] string   aggregate   = "average",
            [FromUri] int      intervalSec = 3600)
        {
            var sw = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(tags))
                return BadRequest("'tags' parameter is required");

            var tagList = tags.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(t => t.Trim())
                              .ToList();

            int aggId = ResolveAggregateId(aggregate);
            var interval = TimeSpan.FromSeconds(intervalSec);

            try
            {
                var results = await BrokerEngine.ReadProcessedAsync(tagList, from, to, aggId, interval);
                var dto = MapToDto(results);

                return Ok(new ApiResponse<List<TagDataDto>>
                {
                    Data = dto,
                    Meta = new ApiMeta
                    {
                        Count       = dto.Sum(d => d.Count),
                        ExecutionMs = sw.ElapsedMilliseconds,
                        From        = from.ToUniversalTime().ToString("o"),
                        To          = to.ToUniversalTime().ToString("o")
                    }
                });
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }

        /// <summary>
        /// Grafana-friendly single-tag endpoint.
        /// Returns a flat { data: [{t, v, q}], meta: {...} } response so that
        /// the Infinity plugin can use root_selector="data" without needing
        /// array-index notation (data[0].points) which Infinity does not support.
        ///
        /// Example:
        ///   GET /api/read/points?tag=Simulations.Simulator 1.TAG_1&amp;from=...&amp;to=...
        /// </summary>
        [HttpGet]
        [Route("points")]
        public async Task<IHttpActionResult> ReadPoints(
            [FromUri] string   tag,
            [FromUri] DateTime from,
            [FromUri] DateTime to,
            [FromUri] int      maxValues = 10000)
        {
            var sw = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(tag))
                return BadRequest("'tag' parameter is required (single tag path)");

            if (maxValues > 100000) maxValues = 100000;

            try
            {
                var results = await BrokerEngine.ReadRawAsync(new[] { tag.Trim() }, from, to, maxValues);
                var tagResult = results.FirstOrDefault();

                if (tagResult == null || tagResult.Error != null)
                    return InternalServerError(new Exception(tagResult?.Error ?? "No result"));

                var points = tagResult.Points.Select(p => new PointDto
                {
                    T = DateTime.SpecifyKind(p.Timestamp, DateTimeKind.Utc).ToString("o"),
                    V = p.Value,
                    Q = p.Quality
                }).ToList();

                return Ok(new ApiResponse<List<PointDto>>
                {
                    Data = points,
                    Meta = new ApiMeta
                    {
                        Count       = points.Count,
                        ExecutionMs = sw.ElapsedMilliseconds,
                        From        = from.ToUniversalTime().ToString("o"),
                        To          = to.ToUniversalTime().ToString("o")
                    }
                });
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }

        /// <summary>
        /// Grafana-friendly single-tag latest value endpoint.
        /// Returns a flat { data: [{t, v, q}] } for easy Infinity stat panels.
        ///
        /// Example:
        ///   GET /api/read/latest/points?tag=Simulations.Simulator 1.TAG_1
        /// </summary>
        [HttpGet]
        [Route("latest/points")]
        public async Task<IHttpActionResult> ReadLatestPoints(
            [FromUri] string tag,
            [FromUri] int    lookbackMinutes = 60)
        {
            var sw = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(tag))
                return BadRequest("'tag' parameter is required");

            try
            {
                var results = await BrokerEngine.ReadLatestAsync(new[] { tag.Trim() }, lookbackMinutes);
                var tagResult = results.FirstOrDefault();

                var points = tagResult?.Points.Select(p => new PointDto
                {
                    T = DateTime.SpecifyKind(p.Timestamp, DateTimeKind.Utc).ToString("o"),
                    V = p.Value,
                    Q = p.Quality
                }).ToList() ?? new List<PointDto>();

                return Ok(new ApiResponse<List<PointDto>>
                {
                    Data = points,
                    Meta = new ApiMeta { Count = points.Count, ExecutionMs = sw.ElapsedMilliseconds }
                });
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }

        /// <summary>
        /// Grafana table-friendly multi-tag latest values endpoint.
        /// Returns a flat array of { tag, value, timestamp, quality } rows —
        /// one row per tag — so Infinity can use simple selectors without
        /// needing nested array-index notation.
        ///
        /// Example:
        ///   GET /api/read/latest/table?tags=TAG_1,TAG_2&amp;lookbackMinutes=120
        /// </summary>
        [HttpGet]
        [Route("latest/table")]
        public async Task<IHttpActionResult> ReadLatestTable(
            [FromUri] string tags,
            [FromUri] int    lookbackMinutes = 120)
        {
            var sw = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(tags))
                return BadRequest("'tags' parameter is required");

            var tagList = tags.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(t => t.Trim())
                              .ToList();

            try
            {
                var results = await BrokerEngine.ReadLatestAsync(tagList, lookbackMinutes);

                var rows = results.Select(r =>
                {
                    // Prefer a Good-quality point; fall back to any point so
                    // Constant/Uncertain values still show in the table.
                    var latest = r.Points.FirstOrDefault(p => p.IsGood)
                                 ?? r.Points.FirstOrDefault(p => p.Value != null)
                                 ?? r.Points.FirstOrDefault();
                    return new
                    {
                        tag       = r.TagName,
                        value     = latest?.Value,
                        timestamp = latest != null
                                    ? DateTime.SpecifyKind(latest.Timestamp, DateTimeKind.Utc).ToString("o")
                                    : null,
                        quality   = latest?.Quality ?? "No data"
                    };
                }).ToList();

                return Ok(new { data = rows, meta = new { count = rows.Count, executionMs = sw.ElapsedMilliseconds } });
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }

        /// <summary>
        /// List the aggregate functions supported by this historian server.
        /// </summary>

        [HttpGet]
        [Route("aggregates")]
        public async Task<IHttpActionResult> GetAggregates()
        {
            try
            {
                var aggs = await BrokerEngine.GetAggregatesAsync();
                return Ok(new ApiResponse<Dictionary<int, string>>
                {
                    Data = aggs,
                    Meta = new ApiMeta { Count = aggs.Count }
                });
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static List<TagDataDto> MapToDto(List<TagReadResult> results)
        {
            return results.Select(r => new TagDataDto
            {
                Tag    = r.TagName,
                Count  = r.Count,
                Error  = r.Error,
                Points = r.Points.Select(p => new PointDto
                {
                    // Always emit ISO 8601 UTC with trailing Z so Grafana & Power BI
                    // never misinterpret the timestamp as local time (UTC+1 here).
                    T = DateTime.SpecifyKind(p.Timestamp, DateTimeKind.Utc).ToString("o"),
                    V = p.Value,
                    Q = p.Quality
                }).ToList()
            }).ToList();
        }

        /// <summary>
        /// Map friendly aggregate names to OPC HDA aggregate IDs.
        /// </summary>
        private static int ResolveAggregateId(string name)
        {
            switch (name?.ToLowerInvariant())
            {
                case "interpolative": return 1;
                case "average":      return 2;
                case "total":        return 3;
                case "minimum":
                case "min":          return 4;
                case "maximum":
                case "max":          return 5;
                case "count":        return 7;
                case "stdev":        return 13;
                case "range":        return 17;
                case "start":        return 10;
                case "end":          return 11;
                case "delta":        return 16;
                default:
                    if (int.TryParse(name, out int id)) return id;
                    return 2; // default to average
            }
        }
    }
}
