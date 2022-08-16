using Microsoft.AspNetCore.Identity;
using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Models;
using Phoenix.DataHandle.DataEntry.Types;
using Phoenix.DataHandle.DataEntry.Types.Uniques;
using Phoenix.DataHandle.Identity;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Main.Types;
using Phoenix.DataHandle.Repositories;
using User = Phoenix.DataHandle.Main.Models.User;

namespace Phoenix.Api.Ardea.Pullers
{
    public class PersonnelPuller : UserPuller
    {
        public override PostCategory PostCategory => PostCategory.Personnel;

        public PersonnelPuller(
            Dictionary<int, SchoolUnique> schoolUqsDict,
            Dictionary<int, CourseUnique> courseUqsDict,
            ApplicationUserManager appUserManager,
            IUserStore<ApplicationUser> appStore,
            PhoenixContext phoenixContext,
            ILogger logger,
            bool verbose = true,
            string? backendDefPass = null)
            : base(schoolUqsDict, courseUqsDict, appUserManager, appStore, phoenixContext, logger, verbose, backendDefPass)
        {
        }

        public override async Task<List<int>> PullAsync()
        {
            _logger.LogInformation("-----------------------------------------------------------------");
            _logger.LogInformation("Personnel synchronization started.");

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                try
                {
                    var posts = await this.GetPostsForSchoolAsync(schoolUqPair.Value);

                    _logger.LogInformation("{PersonnelNumber} Staff members found for School \"{SchoolUq}\".",
                        posts.Count(), schoolUqPair.Value);

                    var school = (await _schoolRepository.FindUniqueAsync(schoolUqPair.Value))!;
                    var phoneCountryCode = school.SchoolSetting.PhoneCountryCode;

                    foreach (var personnelPost in posts)
                    {
                        var personnelAcf = await WPClientWrapper.GetPersonnelAcfAsync(personnelPost);
                        if (personnelAcf is null)
                        {
                            _logger.LogError("No ACF found for post {Title}", personnelPost.GetTitle());
                            continue;
                        }

                        var phone = UserAcf.PrependPhoneCountryCode(
                            personnelAcf.PhoneString, phoneCountryCode);

                        var appUser = await _appUserManager.FindByPhoneNumberAsync(phone);
                        appUser = await this.PutAppUserAsync(appUser, personnelAcf, schoolUqPair.Value,
                            phoneCountryCode);

                        if (appUser is null)
                            continue;

                        var user = await this._userRepository.FindPrimaryAsync(appUser.Id);
                        user = await this.PutUserAsync(user, personnelAcf, appUser.Id);

                        await this.PutUserToRoleAsync(appUser, personnelAcf.Role);
                        await this.PutUserToSchoolAsync(user, school);
                        await this.PutUserToCoursesAsync(user, personnelAcf, school);

                        PulledIds.Add(appUser.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("{Msg}", ex.Message);
                    _logger.LogWarning("Skipping post...");
                    continue;
                }
            }

            _logger.LogInformation("Personnel synchronization finished.");
            _logger.LogInformation("-----------------------------------------------------------------");

            return PulledIds = PulledIds.Distinct().ToList();
        }

        public override async Task<List<int>> ObviateAsync(List<int> toKeep)
        {
            IEnumerable<User> findPersonnelForSchool(int schoolId)
            {
                var school = Task.Run(() => _schoolRepository.FindPrimaryAsync(schoolId)).Result;
                if (school is null)
                    return Enumerable.Empty<User>();

                var users = school.Users.ToList();
                var personnel = new List<User>();

                foreach (var user in users)
                {
                    var appUser = _appUserManager.FindByIdAsync(user.AspNetUserId.ToString()).Result;
                    var roles = _appUserManager.GetRoleRanksAsync(appUser).Result;

                    if (roles.Any(r => r.IsStaffOrBackend()))
                        personnel.Add(user);
                }

                return personnel;
            }

            return ObviatedIds = await ObviateAllPerSchoolAsync(findPersonnelForSchool, _userRepository, toKeep);
        }

        protected override async Task<List<int>> ObviateGroupAsync(IList<User> toObviate, ObviableRepository<User> userRepository)
        {
            var obviatedIds = await base.ObviateGroupAsync(toObviate, userRepository);
            if (obviatedIds.Any())
                await this.DeassignRolesFromObviatedAsync(toObviate, RoleHierarchy.StaffRolesBase);

            return obviatedIds;
        }
    }
}
