using Microsoft.AspNetCore.Mvc;
using Phoenix.Api.Ardea.Pullers;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.WordPress.Models.Uniques;
using Phoenix.DataHandle.WordPress.Wrappers;

namespace Phoenix.Api.Ardea.Controllers
{
    [ApiController]
    [Route("sync")]
    public class SyncController : ControllerBase
    {
        private readonly ILogger<SyncController> _logger;
        private readonly PhoenixContext _phoenixContext;

        public SyncController(
            ILogger<SyncController> logger, 
            PhoenixContext phoenixContext, 
            IConfiguration configuration)
        {
            _logger = logger;
            _phoenixContext = phoenixContext;

            string wpUsername = configuration["WordPressAuth:Username"];
            string wpPassword = configuration["WordPressAuth:Password"];

            bool authenticated = Task.Run(() => WordPressClientWrapper.AuthenticateAsync(wpUsername, wpPassword)).Result;
            if (authenticated)
                _logger.LogInformation("Service authenticated to WordPress successfuly.");
            else
                _logger.LogError("Service was unable to authenticate to WordPress.");

            WordPressClientWrapper.AlwaysUseAuthentication = true;
        }

        [HttpPut("schools")]
        public async Task<IActionResult> PutSchoolDataAsync(string? specificSchool = null, bool verbose = true)
        {
            SchoolUnique? specificSchoolUq = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(specificSchool))
                    specificSchoolUq = new(specificSchool);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }

            SchoolPuller schoolPuller = new(_phoenixContext, _logger, specificSchoolUq, verbose);
            await schoolPuller.PutAsync();
            var schoolUqsDict = schoolPuller.SchoolUqsDict;

            CoursePuller coursePuller = 
                new(schoolUqsDict, _phoenixContext, _logger, specificSchoolUq, verbose);
            await coursePuller.PutAsync();
            var courseUqsDict = coursePuller.CourseUqsDict;

            SchedulePuller schedulePuller = 
                new(schoolUqsDict, courseUqsDict, _phoenixContext, _logger, specificSchoolUq, verbose);
            // The schedule task starts asynchronously and is not affected by the following await operations
            var scheduleTask = schedulePuller.PutAsync();
            
            PersonnelPuller personnelPuller = 
                new(schoolUqsDict, courseUqsDict, _phoenixContext, _logger, specificSchoolUq, verbose);
            await personnelPuller.PutAsync();

            ClientPuller clientPuller = 
                new(schoolUqsDict, courseUqsDict, _phoenixContext, _logger, specificSchoolUq, verbose);
            await clientPuller.PutAsync();

            await scheduleTask;

            return Ok();
        }
    }
}
