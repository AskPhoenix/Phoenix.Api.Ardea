using Microsoft.AspNetCore.Mvc;
using Phoenix.Api.Ardea.Pullers;
using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Models.Uniques;
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
        // TODO: Inject
        //private readonly ApplicationStore _appStore;

        private readonly bool _verbose = true;

        public SyncController(
            ILogger<SyncController> logger,
            PhoenixContext phoenixContext,
            //ApplicationStore applicationStore,
            IConfiguration configuration)
        {
            _logger = logger;
            _phoenixContext = phoenixContext;
            //_appStore = applicationStore;
            
            if (bool.TryParse(configuration["Verbose"], out bool verbose))
                _verbose = verbose;

            string wpUsername = configuration["WordPressAuth:Username"];
            string wpPassword = configuration["WordPressAuth:Password"];

            bool authenticated = false;
            try
            {
                authenticated = Task.Run(() => WPClientWrapper.AuthenticateAsync(wpUsername, wpPassword)).Result;
                WPClientWrapper.AlwaysUseAuthentication = true;
            }
            catch(Exception ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
            }
            finally
            {
                if (authenticated)
                    _logger.LogInformation("Service authenticated to WordPress successfuly.");
                else
                    _logger.LogError("Service was unable to authenticate to WordPress.");
            }
        }

        [HttpPut("/specific/post/{title}")]
        [SwaggerOperation(Summary = "Synchronize data for a specific school by the WP post title.")]
        [SwaggerResponse(StatusCodes.Status200OK, "Data synchronization finished with no problems.")]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "The \"post title\" parameter is mal-formed.")]
        public async Task<IActionResult> PutSchoolDataAsync(
            [SwaggerParameter(Description = "The WP post title.", Required = true)]
            string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return BadRequest();

            SchoolUnique specificSchoolUq;
            try
            {
                specificSchoolUq = new(title);
            }
            catch (ArgumentException ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return BadRequest(ex.Message);
            }

            return await PutSchoolDataAsync(specificSchoolUq);
        }

        [HttpPut("/specific/code/{code}")]
        [SwaggerOperation(Summary = "Synchronize data for a specific school by the school code.")]
        [SwaggerResponse(StatusCodes.Status200OK, "Data synchronization finished with no problems.")]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "The \"school code\" parameter is mal-formed.")]
        public async Task<IActionResult> PutSchoolDataAsync(
            [SwaggerParameter(Description = "The school code.", Required = true)]
            int code)
        {
            SchoolUnique specificSchoolUq;
            try
            {
                specificSchoolUq = new(code);
            }
            catch (ArgumentException ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return BadRequest(ex.Message);
            }

            return await PutSchoolDataAsync(specificSchoolUq);
        }

        [HttpPut("/all")]
        [SwaggerOperation(Summary = "Synchronize data for all schools.")]
        [SwaggerResponse(StatusCodes.Status200OK, "Data synchronization finished with no problems.")]
        public async Task<IActionResult> PutSchoolDataAsync()
        {
            return await PutSchoolDataAsync(specificSchoolUq: null);
        }

        private async Task<IActionResult> PutSchoolDataAsync(SchoolUnique? specificSchoolUq)
        {
            try
            {
                // Always await async calls on the same DBContext instance

                SchoolPuller schoolPuller = new(_phoenixContext, _logger, specificSchoolUq, _verbose);
                await schoolPuller.PutAsync();
                var schoolUqsDict = schoolPuller.SchoolUqsDict;

                CoursePuller coursePuller = new(schoolUqsDict, _phoenixContext, _logger, _verbose);
                await coursePuller.PutAsync();
                var courseUqsDict = coursePuller.CourseUqsDict;

                SchedulePuller schedulePuller = new(schoolUqsDict, courseUqsDict, _phoenixContext, _logger, _verbose);
                await schedulePuller.PutAsync();

                return Ok();

                //PersonnelPuller personnelPuller = new(schoolUqsDict, courseUqsDict, _phoenixContext, _appStore, _logger, _verbose);
                //await personnelPuller.PutAsync();

                //ClientPuller clientPuller = new(schoolUqsDict, courseUqsDict, _phoenixContext, _appStore, _logger, _verbose);
                //await clientPuller.PutAsync();
            }
            catch (Exception ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }
    }
}
