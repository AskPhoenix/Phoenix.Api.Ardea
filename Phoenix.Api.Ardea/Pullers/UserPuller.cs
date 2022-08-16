using Microsoft.AspNetCore.Identity;
using Phoenix.DataHandle.DataEntry.Models;
using Phoenix.DataHandle.DataEntry.Types.Uniques;
using Phoenix.DataHandle.Identity;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Main.Types;
using Phoenix.DataHandle.Repositories;

namespace Phoenix.Api.Ardea.Pullers
{
    public abstract class UserPuller : WPPuller<User>
    {
        protected readonly UserRepository _userRepository;
        protected readonly CourseRepository _courseRepository;

        protected readonly ApplicationUserManager _appUserManager;
        protected readonly ApplicationStore _appStore;

        private string? BackendDefPass { get; }

        public UserPuller(
            Dictionary<int, SchoolUnique> schoolUqsDict,
            Dictionary<int, CourseUnique> courseUqsDict,
            ApplicationUserManager appUserManager,
            IUserStore<ApplicationUser> appStore,
            PhoenixContext phoenixContext,
            ILogger logger,
            bool verbose = true,
            string? backendDefPass = null)
            : base(schoolUqsDict, courseUqsDict, phoenixContext, logger, verbose)
        {
            this.BackendDefPass = backendDefPass;

            _userRepository = new(phoenixContext);
            _courseRepository = new(phoenixContext);

            _appUserManager = appUserManager;
            _appStore = (ApplicationStore)appStore;

            _userRepository.Include(u => u.Schools);
            _userRepository.Include(u => u.Courses);
        }

        protected async Task<ApplicationUser?> PutAppUserAsync(ApplicationUser? appUser, UserAcf userAcf,
            SchoolUnique schoolUq)
        {
            string? phone;
            if (userAcf.Role == RoleRank.Student)
                phone = ((ClientAcf)userAcf).StudentPhoneString;
            else
                phone = userAcf.PhoneString;

            var username = userAcf.GenerateUserName(schoolUq);

            if (appUser is null)
            {
                if (Verbose)
                    _logger.LogInformation("Creating Application User with phone number \"{UserPhone}\"...",
                        userAcf.PhoneString);

                appUser = Activator.CreateInstance<ApplicationUser>();

                await _appStore.SetUserNameAsync(appUser, username);
                await _appStore.SetNormalizedUserNameAsync(appUser, ApplicationUser.NormFunc(username));

                await _appStore.SetPhoneNumberAsync(appUser, phone);
                await _appStore.SetPhoneNumberConfirmedAsync(appUser, false);

                var identityRes = await _appUserManager.CreateAsync(appUser);
                if (identityRes.Succeeded)
                {
                    if (Verbose)
                        _logger.LogInformation("Application User created successfully.");
                }
                else
                {
                    if (Verbose)
                        _logger.LogError("Application User could not be created.");

                    return null;
                }

                // Create Password for Backend Users
                if (userAcf.Role.IsBackend())
                    await _appUserManager.AddPasswordAsync(appUser, this.BackendDefPass);
            }
            else
            {
                if (Verbose)
                    _logger.LogInformation("Updating Application User with phone number \"{UserPhone}\"...",
                        userAcf.PhoneString);

                await _appStore.SetUserNameAsync(appUser, username);
                await _appStore.SetNormalizedUserNameAsync(appUser, ApplicationUser.NormFunc(username));

                // Applies only to students who became self-determined
                await _appStore.SetPhoneNumberAsync(appUser, phone);

                var identityRes = await _appUserManager.UpdateAsync(appUser);
                if (identityRes.Succeeded)
                {
                    if (Verbose)
                        _logger.LogInformation("Application User updated successfully.");
                }
                else
                {
                    if (Verbose)
                        _logger.LogError("Application User could not be updated.");

                    return null;
                }
            }

            return appUser;
        }

        protected async Task<User> PutUserAsync(User? user, UserAcf userAcf, int aspNetUserId)
        {
            if (user is null)
            {
                user = userAcf.ToUser(aspNetUserId);
                await _userRepository.CreateAsync(user);

                if (Verbose)
                    _logger.LogInformation("Phoenix User created successfully.");
            }
            else
            {
                userAcf.ToUser(user, aspNetUserId);
                await _userRepository.UpdateAsync(user);
                await _userRepository.RestoreAsync(user);

                if (Verbose)
                    _logger.LogInformation("Phoenix User updated successfully.");
            }

            return user;
        }

        protected async Task PutUserToRoleAsync(ApplicationUser appUser, RoleRank roleRank)
        {
            if (Verbose)
                _logger.LogInformation("Assigning the role \"{Role}\"...", roleRank.ToFriendlyString());

            if (roleRank.IsStaffOrBackend())
                await _appUserManager.RemoveFromAllButClientRolesAsync(appUser);
            else if (roleRank.IsClient())
                await _appUserManager.RemoveFromAllButPersonnelRolesAsync(appUser);
            else
                await _appUserManager.RemoveFromAllButOneRoleAsync(appUser, roleRank.ToNormalizedString());

            if (!await _appUserManager.IsInRoleAsync(appUser, roleRank.ToNormalizedString()))
                await _appUserManager.AddToRoleAsync(appUser, roleRank.ToNormalizedString());

            if (Verbose)
                _logger.LogInformation("Role \"{Role}\" assigned successfully.", roleRank.ToFriendlyString());

        }

        protected async Task PutUserToSchoolAsync(User user, School school)
        {
            if (!user.Schools.Contains(school))
            {
                if (Verbose)
                    _logger.LogInformation("Subscribing to school...");

                user.Schools.Add(school);
                await _userRepository.UpdateAsync(user);

                if (Verbose)
                    _logger.LogInformation("Subscribed to school successfully.");
            }
        }

        protected async Task PutUserToCoursesAsync(User user, UserAcf userAcf, School school)
        {
            if (Verbose)
                _logger.LogInformation("Enrolling to courses...");

            var coursesInitial = user.Courses;
            List<Course> coursesFinal;

            if (!userAcf.CourseCodes.Any())
            {
                coursesFinal = school.Courses.ToList();
            }
            else
            {
                coursesFinal = new();
                foreach (var courseCode in userAcf.CourseCodes)
                {
                    var courseFinal = await _courseRepository.FindUniqueAsync(new(new(school.Code), courseCode));
                    if (courseFinal is null)
                    {
                        _logger.LogError("There is no course with code {CourseCode}.", courseCode);
                        continue;
                    }

                    coursesFinal.Add(courseFinal);
                }
            }

            // Enroll user to courses they're not enrolled
            foreach (var courseFinal in coursesFinal)
                if (!user.Courses.Contains(courseFinal))
                    user.Courses.Add(courseFinal);

            // Remove user from courses they're not enrolled anymore
            foreach (var courseInitial in coursesInitial)
                if (!coursesFinal.Contains(courseInitial))
                    user.Courses.Remove(courseInitial);

            await _userRepository.UpdateAsync(user);

            if (Verbose)
                _logger.LogInformation("Enrolled to {FinalCoursesNum} courses successfully.",
                    coursesFinal.Count);
        }

        protected async Task DeassignRolesFromObviatedAsync(IList<User> toObviate, int roleCatToRemove)
        {
            bool removeClientRoles = roleCatToRemove == RoleHierarchy.ClientRolesBase;

            if (Verbose)
                _logger.LogInformation("Deassigning Roles from obviated Users.");

            foreach (var user in toObviate)
            {
                var appUser = await _appUserManager.FindByIdAsync(user.AspNetUserId.ToString());
                var appRoleRanks = await _appUserManager.GetRoleRanksAsync(appUser);

                if (appRoleRanks.All(r => r.IsStaffOrBackend()) || appRoleRanks.All(r => r.IsClient()))
                {
                    if (Verbose)
                        _logger.LogInformation("Deleting all roles from User with id {UserId}...", appUser.Id);
                    await _appUserManager.RemoveFromRolesAsync(appUser, appRoleRanks.Select(rr => rr.ToNormalizedString()));

                    if (Verbose)
                        _logger.LogInformation("Assigning \"None\" role to User with id {UserId}...", appUser.Id);
                    await _appUserManager.AddToRoleAsync(appUser, RoleRank.None.ToNormalizedString());

                    continue;
                }

                if (Verbose)
                {
                    _logger.LogWarning("User with id {AppUserId} obviation is skipped because they have " +
                        "both personnel and client roles.", appUser.Id);
                    _logger.LogInformation("Deleting {cat} roles from User with id {AppUserId}...",
                        removeClientRoles ? "client" : "personnel", appUser.Id);
                }

                IEnumerable<RoleRank> roleRanksToRemove;
                if (removeClientRoles)
                    roleRanksToRemove = appRoleRanks.Where(r => r.IsClient());
                else
                    roleRanksToRemove = appRoleRanks.Where(r => r.IsStaffOrBackend());

                var rolesToRemove = roleRanksToRemove.Select(r => r.ToNormalizedString());
                await _appUserManager.RemoveFromRolesAsync(appUser, rolesToRemove);
            }
        }
    }
}
