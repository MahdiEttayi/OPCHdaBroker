// ═══════════════════════════════════════════════════════════════════════════
// OWIN STARTUP — Web API Pipeline Configuration
// ───────────────────────────────────────────────────────────────────────────
// Configures the self-hosted Web API: routing, JSON formatting, CORS, Swagger.
// ═══════════════════════════════════════════════════════════════════════════

using System.Net.Http.Formatting;
using System.Web.Http;
using System.Web.Http.Cors;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Owin;
using Swashbuckle.Application;

namespace OpcHdaBroker.Api
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            var config = new HttpConfiguration();

            // ── Attribute routing ────────────────────────────────────────
            config.MapHttpAttributeRoutes();

            // Fallback conventional route
            config.Routes.MapHttpRoute(
                name:          "DefaultApi",
                routeTemplate: "api/{controller}/{action}/{id}",
                defaults:      new { id = RouteParameter.Optional }
            );

            // ── JSON formatting ──────────────────────────────────────────
            config.Formatters.Clear();
            config.Formatters.Add(new JsonMediaTypeFormatter
            {
                SerializerSettings = new JsonSerializerSettings
                {
                    ContractResolver  = new CamelCasePropertyNamesContractResolver(),
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting        = Formatting.Indented,
                    DateFormatString  = "yyyy-MM-ddTHH:mm:ss.fffZ"
                }
            });

            // ── CORS — allow all origins for development ─────────────────
            var cors = new EnableCorsAttribute("*", "*", "*");
            config.EnableCors(cors);

            // ── Swagger ──────────────────────────────────────────────────
            config.EnableSwagger(c =>
            {
                c.SingleApiVersion("v1", "OPC HDA Broker API")
                 .Description("REST proxy for KepServerEX Local Historian (OPC HDA). "
                            + "Query raw and aggregated historical data from TSD files.");
            })
            .EnableSwaggerUi();

            config.EnsureInitialized();
            app.UseWebApi(config);
        }
    }
}
