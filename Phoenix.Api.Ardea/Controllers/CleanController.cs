using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phoenix.DataHandle.Identity;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using Swashbuckle.AspNetCore.Annotations;

namespace Phoenix.Api.Ardea.Controllers
{
    [Authorize(AuthenticationSchemes = "Bearer")]
    [ApiController]
    [Route("api/[controller]")]
    public class CleanController : ControllerBase
    {
        private readonly ILogger<CleanController> _logger;
        private readonly ApplicationUserManager _appUserManager;

        private readonly SchoolRepository _schoolRepository;
        private readonly CourseRepository _courseRepository;
        private readonly ScheduleRepository _scheduleRepository;
        private readonly ClassroomRepository _classroomRepository;
        private readonly LectureRepository _lectureRepository;
        private readonly UserRepository _userRepository;

        private const string MSG200 = "Data deletion finished with no problems.";

        public CleanController(
            ILogger<CleanController> logger,
            PhoenixContext phoenixContext,
            ApplicationUserManager appUserManager)
        {
            _logger = logger;
            _appUserManager = appUserManager;

            _schoolRepository = new(phoenixContext);
            _courseRepository = new(phoenixContext);
            _scheduleRepository = new(phoenixContext);
            _classroomRepository = new(phoenixContext);
            _lectureRepository = new(phoenixContext);
            _userRepository = new(phoenixContext);
        }

        [HttpDelete]
        [SwaggerOperation(Summary = "Clean (delete) all obviated data.")]
        [SwaggerResponse(StatusCodes.Status200OK, MSG200)]
        public async Task<IActionResult> DeleteObviatedForSchoolAsync()
        {
            _logger.LogInformation("Deleting obviated Schools...");
            await _schoolRepository.DeleteAllObviatedAsync();

            _logger.LogInformation("Deleting obviated Courses...");
            await _courseRepository.DeleteAllObviatedAsync();

            _logger.LogInformation("Deleting obviated Schedules...");
            await _scheduleRepository.DeleteAllObviatedAsync();

            _logger.LogInformation("Deleting obviated Classrooms...");
            await _classroomRepository.DeleteAllObviatedAsync();

            _logger.LogInformation("Deleting obviated Lectures...");
            await _lectureRepository.DeleteAllObviatedAsync();

            _logger.LogInformation("Deleting obviated Users...");
            var obviatedUsers = _userRepository.Find()
                .Where(u => u.ObviatedAt.HasValue);

            ApplicationUser obviatedAppUser;
            foreach (var obviatedUser in obviatedUsers)
            {
                obviatedAppUser = await _appUserManager.FindByIdAsync(obviatedUser.AspNetUserId.ToString());
                if (obviatedAppUser is null)
                    continue;

                await _appUserManager.DeleteAsync(obviatedAppUser);
            }

            await _userRepository.DeleteAllObviatedAsync();

            return Ok();
        }
    }
}
