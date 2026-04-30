// ═══════════════════════════════════════════════════════════════════════════
// TAGS CONTROLLER
// ───────────────────────────────────────────────────────────────────────────
// GET  /api/tags          — List all known tags
// POST /api/tags/add      — Register new tags (body: ["tag1","tag2"])
// POST /api/tags/refresh  — Force refresh the tag cache
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
    [RoutePrefix("api/tags")]
    public class TagsController : ApiController
    {
        [HttpGet]
        [Route("")]
        public async Task<IHttpActionResult> GetTags(
            string search = null,
            int    limit   = 1000,
            int    offset  = 0)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var allTags = await BrokerEngine.GetTagsAsync();
                var filtered = string.IsNullOrWhiteSpace(search)
                    ? allTags
                    : allTags.Where(t => t.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                var page = filtered
                    .Skip(offset)
                    .Take(Math.Min(limit, 10000))
                    .Select(t => new TagInfoDto
                    {
                        ItemId      = t,
                        DisplayPath = t.Replace(".", " > ")
                    })
                    .ToList();

                return Ok(new ApiResponse<List<TagInfoDto>>
                {
                    Data = page,
                    Meta = new ApiMeta
                    {
                        Count       = page.Count,
                        ExecutionMs = sw.ElapsedMilliseconds
                    }
                });
            }
            catch (Exception ex) { return InternalServerError(ex); }
        }

        /// <summary>
        /// Register new tag paths. The broker will attempt to read these from KepServerEX.
        /// Body: ["PLF_A10.A10.10QT0002", "PLF_A10.A10.10QT0003"]
        /// </summary>
        [HttpPost]
        [Route("add")]
        public async Task<IHttpActionResult> AddTags([FromBody] List<string> newTags)
        {
            var sw = Stopwatch.StartNew();
            if (newTags == null || newTags.Count == 0)
                return BadRequest("Body must be a JSON array of tag paths");

            try
            {
                int added = await BrokerEngine.AddTagsAsync(newTags);
                var allTags = await BrokerEngine.GetTagsAsync();

                return Ok(new ApiResponse<object>
                {
                    Data = new { added, totalTags = allTags.Count },
                    Meta = new ApiMeta { Count = allTags.Count, ExecutionMs = sw.ElapsedMilliseconds }
                });
            }
            catch (Exception ex) { return InternalServerError(ex); }
        }

        [HttpPost]
        [Route("refresh")]
        public async Task<IHttpActionResult> RefreshTags()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                BrokerEngine.Cache.Invalidate("tags");
                var tags = await BrokerEngine.GetTagsAsync();
                return Ok(new ApiResponse<object>
                {
                    Data = new { refreshed = true, tagCount = tags.Count },
                    Meta = new ApiMeta { Count = tags.Count, ExecutionMs = sw.ElapsedMilliseconds }
                });
            }
            catch (Exception ex) { return InternalServerError(ex); }
        }
    }
}
