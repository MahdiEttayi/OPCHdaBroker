// ═══════════════════════════════════════════════════════════════════════════
// DIAGNOSTICS CONTROLLER
// ───────────────────────────────────────────────────────────────────────────
// Exposes the comprehensive diagnostic runner via REST API.
// GET /api/diagnostics — runs all tests and returns a full report.
// ═══════════════════════════════════════════════════════════════════════════

using System.Threading.Tasks;
using System.Web.Http;
using OpcHdaBroker.Diagnostics;

namespace OpcHdaBroker.Api.Controllers
{
    [RoutePrefix("api/diagnostics")]
    public class DiagnosticsController : ApiController
    {
        /// <summary>
        /// Run comprehensive diagnostics on the OPC HDA connection.
        /// Tests SDK methods, tag path formats, COM QI, and threading.
        /// </summary>
        [HttpGet, Route("")]
        public async Task<IHttpActionResult> RunDiagnostics()
        {
            var report = await BrokerEngine.RunDiagnosticsAsync();
            return Ok(report);
        }
    }
}
