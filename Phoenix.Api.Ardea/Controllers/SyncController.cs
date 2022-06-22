using Microsoft.AspNetCore.Mvc;
using Phoenix.Api.Ardea.Pullers;
using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Models.Uniques;
using Phoenix.DataHandle.Identity;
using Phoenix.DataHandle.Main.Models;
using Swashbuckle.AspNetCore.Annotations;
using WordPressPCL.Models.Exceptions;

namespace Phoenix.Api.Ardea.Controllers
{
    [ApiController]
    [Route("sync")]
    public class SyncController : ControllerBase
    {
        private readonly ILogger<SyncController> _logger;
        private readonly PhoenixContext _phoenixContext;
        //private readonly ApplicationStore _appStore;    // TODO: Inject

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

        [HttpPut("schools/post/{title}")]
        [SwaggerOperation(Summary = "Synchronize schools' data with the Phoenix backend.")]
        [SwaggerResponse(StatusCodes.Status200OK, "Data synchronization finished with no problems.")]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "The \"specific school\" parameter is mal-formed.")]
        public async Task<IActionResult> PutSchoolDataAsync(
            [SwaggerParameter(Description = "Specify only one school to update by its WordPress post title.", Required = false)]
            string? title = null)
        {
            SchoolUnique? specificSchoolUq = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(title))
                    specificSchoolUq = new(title);
            }
            catch (ArgumentException ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return BadRequest(ex.Message);
            }

            return await PutSchoolDataAsync(specificSchoolUq);
        }

        [HttpPut("schools/code/{code}")]
        [SwaggerOperation(Summary = "Synchronize schools' data with the Phoenix backend.")]
        [SwaggerResponse(StatusCodes.Status200OK, "Data synchronization finished with no problems.")]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "The \"specific school\" parameter is mal-formed.")]
        public async Task<IActionResult> PutSchoolDataAsync(
            [SwaggerParameter(Description = "Specify only one school to update by its unique code.", Required = false)]
            int? code)
        {
            SchoolUnique? specificSchoolUq = null;
            try
            {
                if (code != null)
                    specificSchoolUq = new(code.Value);
            }
            catch (ArgumentException ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return BadRequest(ex.Message);
            }

            return await PutSchoolDataAsync(specificSchoolUq);
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

                //PersonnelPuller personnelPuller = new(schoolUqsDict, courseUqsDict, _phoenixContext, _appStore, _logger, _verbose);
                //await personnelPuller.PutAsync();

                //ClientPuller clientPuller = new(schoolUqsDict, courseUqsDict, _phoenixContext, _appStore, _logger, _verbose);
                //await clientPuller.PutAsync();

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }
    }
}
