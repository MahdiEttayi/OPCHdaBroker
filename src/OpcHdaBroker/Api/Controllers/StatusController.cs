// ═══════════════════════════════════════════════════════════════════════════
// STATUS CONTROLLER
// ───────────────────────────────────────────────────────────────────────────
// GET /api/status  — Broker and historian health information
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Web.Http;
using OpcHdaBroker.Api.Models;

namespace OpcHdaBroker.Api.Controllers
{
    [RoutePrefix("api")]
    public class StatusController : ApiController
    {
        /// <summary>
        /// Returns broker health and historian server status.
        /// Used for monitoring and diagnostics.
        /// </summary>
        [HttpGet]
        [Route("status")]
        public async Task<IHttpActionResult> GetStatus()
        {
            var sw = Stopwatch.StartNew();

            try
            {
                var status = await BrokerEngine.GetStatusAsync();
                status.ExecutionMs = sw.ElapsedMilliseconds;
                return Ok(status);
            }
            catch (Exception ex)
            {
                return Ok(new StatusDto
                {
                    Connected    = false,
                    ServerStatus = "Error: " + ex.Message,
                    BrokerUptime = BrokerEngine.GetUptime().ToString(@"d\.hh\:mm\:ss")
                });
            }
        }

        /// <summary>
        /// Grafana Infinity-friendly status endpoint.
        /// Wraps the status in an array so Infinity v3 column selectors work.
        /// </summary>
        [HttpGet]
        [Route("status/list")]
        public async Task<IHttpActionResult> GetStatusList()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var status = await BrokerEngine.GetStatusAsync();
                status.ExecutionMs = sw.ElapsedMilliseconds;
                return Ok(new[] { status });
            }
            catch (Exception ex)
            {
                return Ok(new[] { new StatusDto
                {
                    Connected    = false,
                    ServerStatus = "Error: " + ex.Message,
                    BrokerUptime = BrokerEngine.GetUptime().ToString(@"d\.hh\:mm\:ss")
                }});
            }
        }

        /// <summary>
        /// Simple liveness probe — always returns 200 if the service is running.
        /// </summary>
        [HttpGet]
        [Route("health")]
        public IHttpActionResult Health()
        {
            return Ok(new { status = "ok", timestamp = DateTime.UtcNow.ToString("o") });
        }
    }

    /// <summary>
    /// Extended status DTO with execution time.
    /// </summary>
    public class StatusDtoExt : StatusDto
    {
        public long ExecutionMs { get; set; }
    }
}
