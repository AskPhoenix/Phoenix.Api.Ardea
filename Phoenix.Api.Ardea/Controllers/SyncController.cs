using Microsoft.AspNetCore.Mvc;
using Phoenix.Api.Ardea.Pullers;
using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Models.Uniques;
using Phoenix.DataHandle.Identity;
using Phoenix.DataHandle.Main.Models;
using Swashbuckle.AspNetCore.Annotations;

namespace Phoenix.Api.Ardea.Controllers
{
    [ApiController]
    [Route("sync")]
    public class SyncController : ControllerBase
    {
        private readonly ILogger<SyncController> _logger;
        private readonly PhoenixContext _phoenixContext;
        private readonly ApplicationStore _appStore;    // TODO: Inject

        public SyncController(
            ILogger<SyncController> logger,
            PhoenixContext phoenixContext,
            ApplicationStore applicationStore,
            IConfiguration configuration)
        {
            _logger = logger;
            _phoenixContext = phoenixContext;
            _appStore = applicationStore;

            string wpUsername = configuration["WordPressAuth:Username"];
            string wpPassword = configuration["WordPressAuth:Password"];

            bool authenticated = Task.Run(() => WPClientWrapper.AuthenticateAsync(wpUsername, wpPassword)).Result;
            if (authenticated)
                _logger.LogInformation("Service authenticated to WordPress successfuly.");
            else
                _logger.LogError("Service was unable to authenticate to WordPress.");

            WPClientWrapper.AlwaysUseAuthentication = true;
        }

        [HttpPut("schools")]
        [SwaggerOperation(Summary = "Synchronize schools' data with the Phoenix backend.")]
        [SwaggerResponse(StatusCodes.Status200OK, "Data synchronization finished with no problems.")]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "The \"specific school\" parameter is mal-formed.")]
        public async Task<IActionResult> PutSchoolDataAsync(
            [SwaggerParameter(Description = "Specify only one school to update by its WordPress post title.", Required = false)]
            string? specificSchool = null, 
            [SwaggerParameter(Description = "Switch between \"verbose\" and \"quiet\" logging.", Required = false)]
            bool verbose = true)
        {
            SchoolUnique? specificSchoolUq = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(specificSchool))
                    specificSchoolUq = new(specificSchool);
            }
            catch (ArgumentException ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return BadRequest(ex.Message);
            }

            // Always await async calls on the same DBContext instance

            SchoolPuller schoolPuller = new(_phoenixContext, _logger, specificSchoolUq, verbose);
            await schoolPuller.PutAsync();
            var schoolUqsDict = schoolPuller.SchoolUqsDict;

            CoursePuller coursePuller = new(schoolUqsDict, _phoenixContext, _logger, verbose);
            await coursePuller.PutAsync();
            var courseUqsDict = coursePuller.CourseUqsDict;

            SchedulePuller schedulePuller = new(schoolUqsDict, courseUqsDict, _phoenixContext, _logger, verbose);
            await schedulePuller.PutAsync();
            
            //PersonnelPuller personnelPuller = new(schoolUqsDict, courseUqsDict, _phoenixContext, _appStore, _logger, verbose);
            //await personnelPuller.PutAsync();

            //ClientPuller clientPuller = new(schoolUqsDict, courseUqsDict, _phoenixContext, _appStore, _logger, verbose);
            //await clientPuller.PutAsync();

            return Ok();
        }
    }
}
