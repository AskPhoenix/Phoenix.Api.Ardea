using Microsoft.AspNetCore.Mvc;
using Phoenix.Api.Ardea.Pullers;
using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Types.Uniques;
using Phoenix.DataHandle.Identity;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using Swashbuckle.AspNetCore.Annotations;

namespace Phoenix.Api.Ardea.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SyncController : ControllerBase
    {
        private readonly ILogger<SyncController> _logger;
        private readonly PhoenixContext _phoenixContext;
        private readonly ApplicationUserManager _appUserManager;
        private readonly IConfiguration _configuration;
        private readonly SchoolRepository _schoolRepository;

        private readonly bool _verbose = true;

        private const string MSG200 = "Data synchronization finished with no problems.";
        private const string MSG400 = "The \"school code\" parameter is mal-formed.";
        private const string ERR_SC = "There is no school with the specified code.";
        private const string DES_P1 = "The school code.";

        public SyncController(
            ILogger<SyncController> logger,
            PhoenixContext phoenixContext,
            ApplicationUserManager appUserManager,
            IConfiguration configuration)
        {
            _logger = logger;
            _phoenixContext = phoenixContext;
            _appUserManager = appUserManager;
            _configuration = configuration;
            _schoolRepository = new(phoenixContext);
            
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

        private bool TryGetSchoolUq(int code, out SchoolUnique schoolUq)
        {
            try
            {
                schoolUq = new(code);
                return true;
            }
            catch (ArgumentException ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
            }

            schoolUq = new(0);
            return false;
        }

        private async Task<Tuple<Dictionary<int, SchoolUnique>, Dictionary<int, CourseUnique>>> PutInitAsync(int code)
        {
            if (!TryGetSchoolUq(code, out SchoolUnique specificSchoolUq))
                throw new ArgumentException(MSG400);

            var school = await _schoolRepository.FindUniqueAsync(specificSchoolUq);
            if (school is null)
                throw new ArgumentException(ERR_SC);

            var schoolUqsDict = new Dictionary<int, SchoolUnique>(1) { { school.Id, specificSchoolUq } };
            var courseUqsDict = new Dictionary<int, CourseUnique>(
                school.Courses.Select(c => new KeyValuePair<int, CourseUnique>(c.Id, new(specificSchoolUq, c.Code))));

            return new(schoolUqsDict, courseUqsDict);
        }

        [HttpPut("specific/{code}")]
        [SwaggerOperation(Summary = "Synchronize all data for a specific school by its code.")]
        [SwaggerResponse(StatusCodes.Status200OK, MSG200)]
        [SwaggerResponse(StatusCodes.Status400BadRequest, MSG400)]
        
        public async Task<IActionResult> PutAllForSchoolAsync(
            [SwaggerParameter(Description = DES_P1, Required = true)]
            int code)
        {
            if (!TryGetSchoolUq(code, out SchoolUnique specificSchoolUq))
                return BadRequest(MSG400);

            return await this.PutAllAsync(specificSchoolUq);
        }

        [HttpPut("specific/{code}/school_info")]
        [SwaggerOperation(Summary = "Synchronize school info for a specific school by its code.")]
        [SwaggerResponse(StatusCodes.Status200OK, MSG200)]
        [SwaggerResponse(StatusCodes.Status400BadRequest, MSG400)]

        public async Task<IActionResult> PutSchoolInfoForSchoolAsync(
            [SwaggerParameter(Description = DES_P1, Required = true)]
            int code)
        {
            if (!TryGetSchoolUq(code, out SchoolUnique specificSchoolUq))
                return BadRequest(MSG400);

            SchoolPuller schoolPuller = new(specificSchoolUq, _phoenixContext, _logger, _verbose);
            await schoolPuller.PutAsync();

            return Ok();
        }

        [HttpPut("specific/{code}/courses")]
        [SwaggerOperation(Summary = "Synchronize courses for a specific school by its code.")]
        [SwaggerResponse(StatusCodes.Status200OK, MSG200)]
        [SwaggerResponse(StatusCodes.Status400BadRequest, MSG400)]
        public async Task<IActionResult> PutCoursesForSchoolAsync(
            [SwaggerParameter(Description = DES_P1, Required = true)]
            int code)
        {
            try
            {
                var dict = await this.PutInitAsync(code);

                CoursePuller coursePuller = new(dict.Item1, _phoenixContext, _logger, _verbose);
                await coursePuller.PutAsync();
            }
            catch (ArgumentException ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Ok();
        }

        [HttpPut("specific/{code}/schedules")]
        [SwaggerOperation(Summary = "Synchronize schedules for a specific school by its code.")]
        [SwaggerResponse(StatusCodes.Status200OK, MSG200)]
        [SwaggerResponse(StatusCodes.Status400BadRequest, MSG400)]
        public async Task<IActionResult> PutSchedulesForSchoolAsync(
            [SwaggerParameter(Description = DES_P1, Required = true)]
            int code)
        {
            try
            {
                var dict = await this.PutInitAsync(code);

                SchedulePuller schedulePuller = new(dict.Item1, dict.Item2, _phoenixContext, _logger, _verbose);
                await schedulePuller.PutAsync();
            }
            catch (ArgumentException ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Ok();
        }

        [HttpPut("specific/{code}/personnel")]
        [SwaggerOperation(Summary = "Synchronize personnel for a specific school by its code.")]
        [SwaggerResponse(StatusCodes.Status200OK, MSG200)]
        [SwaggerResponse(StatusCodes.Status400BadRequest, MSG400)]
        public async Task<IActionResult> PutPersonnelForSchoolAsync(
            [SwaggerParameter(Description = DES_P1, Required = true)]
            int code)
        {
            try
            {
                var dict = await this.PutInitAsync(code);

                PersonnelPuller personnelPuller = new(dict.Item1, dict.Item2,
                    _appUserManager, _configuration["BackendDefPass"], _phoenixContext, _logger, _verbose);
                await personnelPuller.PutAsync();
            }
            catch (ArgumentException ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Ok();
        }

        [HttpPut("specific/{code}/clients")]
        [SwaggerOperation(Summary = "Synchronize clients for a specific school by its code.")]
        [SwaggerResponse(StatusCodes.Status200OK, MSG200)]
        [SwaggerResponse(StatusCodes.Status400BadRequest, MSG400)]
        public async Task<IActionResult> PutClientsForSchoolAsync(
            [SwaggerParameter(Description = DES_P1, Required = true)]
            int code)
        {
            try
            {
                var dict = await this.PutInitAsync(code);

                ClientPuller clientPuller = new(dict.Item1, dict.Item2,
                    _appUserManager, _phoenixContext, _logger, _verbose);
                await clientPuller.PutAsync();
            }
            catch (ArgumentException ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogCritical("{ExceptionMsg}", ex.Message);
                return StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Ok();
        }

        // Hide
        [HttpPut("all")]
        [SwaggerOperation(Summary = "Synchronize all data for all schools.")]
        [SwaggerResponse(StatusCodes.Status200OK, MSG200)]
        private async Task<IActionResult> PutAllAsync()
        {
            return await this.PutAllAsync(specificSchoolUq: null);
        }

        private async Task<IActionResult> PutAllAsync(SchoolUnique? specificSchoolUq)
        {
            try
            {
                // Always await async calls on the same DBContext instance

                SchoolPuller schoolPuller = new(specificSchoolUq, _phoenixContext, _logger, _verbose);
                await schoolPuller.PutAsync();
                var schoolUqsDict = schoolPuller.SchoolUqsDict;

                CoursePuller coursePuller = new(schoolUqsDict, _phoenixContext, _logger, _verbose);
                await coursePuller.PutAsync();
                var courseUqsDict = coursePuller.CourseUqsDict;

                SchedulePuller schedulePuller = new(schoolUqsDict, courseUqsDict,
                    _phoenixContext, _logger, _verbose);
                await schedulePuller.PutAsync();

                PersonnelPuller personnelPuller = new(schoolUqsDict, courseUqsDict,
                    _appUserManager, _configuration["BackendDefPass"], _phoenixContext, _logger, _verbose);
                await personnelPuller.PutAsync();

                ClientPuller clientPuller = new(schoolUqsDict, courseUqsDict,
                    _appUserManager, _phoenixContext, _logger, _verbose);
                await clientPuller.PutAsync();

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
