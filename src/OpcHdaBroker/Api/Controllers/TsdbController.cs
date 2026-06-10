// ═══════════════════════════════════════════════════════════════════════════
// TIMESACALEDB CONTROLLER
// ───────────────────────────────────────────────────────────────────────────
// REST endpoints for TimescaleDB status, backfill management, and data
// export control.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Net;
using System.Threading.Tasks;
using System.Web.Http;
using OpcHdaBroker.Api.Models;
using Serilog;

namespace OpcHdaBroker.Api.Controllers
{
    [RoutePrefix("api/timescaledb")]
    public class TsdbController : ApiController
    {
        private static readonly ILogger Log = Serilog.Log.ForContext<TsdbController>();

        [HttpGet]
        [Route("status")]
        public TsdbStatusDto GetStatus()
        {
            try
            {
                return BrokerEngine.GetTimescaleDbStatus();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GetTimescaleDbStatus failed");
                return new TsdbStatusDto { Connected = false, Message = "Internal error — check broker logs" };
            }
        }

        [HttpGet]
        [Route("backfill/status")]
        public BackfillStatusDto GetBackfillStatus()
        {
            try
            {
                var status = BrokerEngine.GetTimescaleDbStatus();
                return status.Backfill ?? new BackfillStatusDto { State = "Not started" };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GetBackfillStatus failed");
                return new BackfillStatusDto { State = "Internal error — check broker logs" };
            }
        }

        [HttpPost]
        [Route("backfill/start")]
        public IHttpActionResult StartBackfill()
        {
            try
            {
                if (!BrokerEngine.IsTimescaleDbEnabled)
                    return Content(HttpStatusCode.ServiceUnavailable, "TimescaleDB not configured.");

                BrokerEngine.StartBackfill();
                return Ok("Backfill started.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "StartBackfill failed");
                return InternalServerError(ex);
            }
        }

        [HttpPost]
        [Route("backfill/pause")]
        public IHttpActionResult PauseBackfill()
        {
            try
            {
                if (!BrokerEngine.IsTimescaleDbEnabled)
                    return Content(HttpStatusCode.ServiceUnavailable, "TimescaleDB not configured.");

                BrokerEngine.PauseBackfill();
                return Ok("Backfill paused.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "PauseBackfill failed");
                return InternalServerError(ex);
            }
        }

        [HttpPost]
        [Route("backfill/resume")]
        public IHttpActionResult ResumeBackfill()
        {
            try
            {
                if (!BrokerEngine.IsTimescaleDbEnabled)
                    return Content(HttpStatusCode.ServiceUnavailable, "TimescaleDB not configured.");

                BrokerEngine.ResumeBackfill();
                return Ok("Backfill resumed.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ResumeBackfill failed");
                return InternalServerError(ex);
            }
        }

        [HttpPost]
        [Route("backfill/stop")]
        public IHttpActionResult StopBackfill()
        {
            try
            {
                if (!BrokerEngine.IsTimescaleDbEnabled)
                    return Content(HttpStatusCode.ServiceUnavailable, "TimescaleDB not configured.");

                BrokerEngine.StopBackfill();
                return Ok("Backfill stopped.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "StopBackfill failed");
                return InternalServerError(ex);
            }
        }

        [HttpPost]
        [Route("backfill/range")]
        public IHttpActionResult RunBackfillRange([FromBody] BackfillRangeRequest request)
        {
            try
            {
                if (!BrokerEngine.IsTimescaleDbEnabled)
                    return Content(HttpStatusCode.ServiceUnavailable, "TimescaleDB not configured.");

                if (request == null || request.StartDate == default || request.EndDate == default)
                    return BadRequest("Provide { startDate: '...', endDate: '...' } in JSON body.");

                if (request.StartDate >= request.EndDate)
                    return BadRequest("StartDate must be before EndDate.");

                Log.Information("[API] Starting custom backfill: {Start} → {End}",
                    request.StartDate, request.EndDate);

                Task.Run(() => BrokerEngine.RunBackfillRange(request.StartDate, request.EndDate));

                return Ok($"Backfill started for range {request.StartDate:yyyy-MM-dd} → {request.EndDate:yyyy-MM-dd}. Check status at /api/timescaledb/backfill/status.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "RunBackfillRange failed");
                return InternalServerError(ex);
            }
        }

        [HttpDelete]
        [Route("data")]
        public IHttpActionResult TruncateData(bool confirm = false)
        {
            try
            {
                if (!BrokerEngine.IsTimescaleDbEnabled)
                    return Content(HttpStatusCode.ServiceUnavailable, "TimescaleDB not configured.");

                if (!confirm)
                    return Content(HttpStatusCode.BadRequest, "Pass ?confirm=true to delete all data.");

                BrokerEngine.TruncateTimescaleDb();
                return Ok("All TimescaleDB data deleted.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TruncateTimescaleDb failed");
                return InternalServerError(ex);
            }
        }
    }

    public class BackfillRangeRequest
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate   { get; set; }
    }
}
