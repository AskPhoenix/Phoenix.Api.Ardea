using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Models.Uniques;
using Phoenix.DataHandle.Identity;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Main.Types;
using Phoenix.DataHandle.Repositories;
using WordPressPCL.Models;
using User = Phoenix.DataHandle.Main.Models.User;

namespace Phoenix.Api.Ardea.Pullers
{
    public class PersonnelPuller : WPPuller<User>
    {
        private readonly UserRepository _userRepository;
        private readonly CourseRepository _courseRepository;

        private readonly ApplicationUserManager _appUserManager;

        private string BackendDefPass { get; }

        public override PostCategory PostCategory => PostCategory.Personnel;

        public PersonnelPuller(Dictionary<int, SchoolUnique> schoolUqsDict, Dictionary<int, CourseUnique> courseUqsDict,
            ApplicationUserManager appUserManager, string backendDefPass,
            PhoenixContext phoenixContext, ILogger logger, bool verbose = true)
            : base(schoolUqsDict, courseUqsDict, phoenixContext, logger, verbose)
        {
            this.BackendDefPass = backendDefPass;

            _userRepository = new(phoenixContext);
            _courseRepository = new(phoenixContext);

            _appUserManager = appUserManager;

            _userRepository.Include(u => u.Schools);
            _userRepository.Include(u => u.Courses);
        }

        // TODO: Reduce code that is common for Personnel & Client Users

        public override async Task<List<int>> PullAsync()
        {
            _logger.LogInformation("-----------------------------------------------------------------");
            _logger.LogInformation("Personnel synchronization started.");

            IEnumerable<Post> personnelPosts = await WPClientWrapper.GetPostsAsync(this.PostCategory);
            IEnumerable<Post> filteredPosts;

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                filteredPosts = personnelPosts.FilterPostsForSchool(schoolUqPair.Value);

                _logger.LogInformation("{PersonnelNumber} Staff members found for School \"{SchoolUq}\".",
                    filteredPosts.Count(), schoolUqPair.Value);

                var school = await _schoolRepository.FindUniqueAsync(schoolUqPair.Value);

                foreach (var personnelPost in filteredPosts)
                {
                    var personnelAcf = await WPClientWrapper.GetPersonnelAcfAsync(personnelPost);
                    var appUser = await _appUserManager.FindByPhoneNumberAsync(personnelAcf.PhoneString);
                    User? user;

                    if (appUser is null)
                    {
                        if (Verbose)
                            _logger.LogInformation("Creating Personnel User with phone number \"{UserPhone}\"...",
                                personnelAcf.PhoneString);

                        // Application User
                        appUser = new ApplicationUser()
                        {
                            PhoneNumber = personnelAcf.PhoneString,
                            PhoneNumberConfirmed = false,
                            UserName = personnelAcf.GenerateUserName(schoolUqPair.Value)
                        }.Normalize();

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

                            continue;
                        }

                        // Create Password for Backend Users
                        if (personnelAcf.Role.IsBackend())
                            await _appUserManager.AddPasswordAsync(appUser, this.BackendDefPass);

                        // Phoenix User
                        user = personnelAcf.ToUser(appUser.Id);
                        await _userRepository.CreateAsync(user);
                        if (Verbose)
                            _logger.LogInformation("Phoenix User created successfully.");
                    }
                    else
                    {
                        if (Verbose)
                            _logger.LogInformation("Updating Personnel User with phone number \"{UserPhone}\"...",
                                personnelAcf.PhoneString);

                        // Application User
                        appUser.UserName = personnelAcf.GenerateUserName(schoolUqPair.Value);
                        appUser.Normalize();

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

                            continue;
                        }

                        // Phoenix User
                        user = await _userRepository.FindPrimaryAsync(appUser.Id);
                        if (user is null)
                        {
                            user = personnelAcf.ToUser(appUser.Id);
                            await _userRepository.CreateAsync(user);

                            if (Verbose)
                                _logger.LogInformation("Phoenix User created successfully.");
                        }
                        else
                        {
                            personnelAcf.ToUser(user, appUser.Id);
                            await _userRepository.UpdateAsync(user);
                            await _userRepository.RestoreAsync(user);

                            if (Verbose)
                                _logger.LogInformation("Phoenix User updated successfully.");
                        }
                    }

                    PulledIds.Add(appUser.Id);

                    // Link Roles
                    if (Verbose)
                        _logger.LogInformation("Assigning the role \"{Role}\"...", personnelAcf.Role.ToFriendlyString());

                    // Delete any other roles the user might have
                    // The only possible scenario where 2 roles are allowed is: a non-client role + parent
                    await _appUserManager.RemoveFromAllRolesButOneAsync(appUser, personnelAcf.Role.ToNormalizedString());

                    if (!await _appUserManager.IsInRoleAsync(appUser, personnelAcf.Role.ToNormalizedString()))
                       await _appUserManager.AddToRoleAsync(appUser, personnelAcf.Role.ToNormalizedString());

                    if (Verbose)
                        _logger.LogInformation("Role \"{Role}\" assigned successfully.", personnelAcf.Role.ToFriendlyString());

                    

                    // Link School
                    if (!user.Schools.Contains(school!))
                    {
                        if (Verbose)
                            _logger.LogInformation("Subscribing to school...");

                        user.Schools.Add(school!);
                        await _userRepository.UpdateAsync(user);

                        if (Verbose)
                            _logger.LogInformation("Subscribed to school successfully.");
                    }

                    // Link Courses
                    // TODO: Use CourseUqsDict?
                    if (Verbose)
                        _logger.LogInformation("Enrolling to courses...");

                    var coursesInitial = user.Courses;
                    List<Course> coursesFinal;

                    if (!personnelAcf.CourseCodes.Any())
                    {
                        coursesFinal = _courseRepository.Find()
                            .Where(c => c.SchoolId == schoolUqPair.Key)
                            .ToList();
                    }
                    else
                    {
                        coursesFinal = new();
                        foreach (var courseCode in personnelAcf.CourseCodes)
                        {
                            var courseFinal = await _courseRepository.FindUniqueAsync(new(schoolUqPair.Value, courseCode));
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

                return school.Users;
            }

            return ObviatedIds = await ObviateAllPerSchoolAsync(findPersonnelForSchool, _userRepository, toKeep);
        }

        protected override async Task<List<int>> ObviateGroupAsync(IList<User> toObviate, ObviableRepository<User> userRepository)
        {
            var obviatedIds = await base.ObviateGroupAsync(toObviate, userRepository);
            await this.DeassignRolesFromObviatedAsync(toObviate);

            return obviatedIds;
        }

        protected async Task DeassignRolesFromObviatedAsync(IList<User> toObviate)
        {
            if (Verbose)
                _logger.LogInformation("Deassigning Roles from obviated Users.");

            foreach (var user in toObviate)
            {
                var appUser = await _appUserManager.FindByIdAsync(user.AspNetUserId.ToString());
                var appRoleRanks = await _appUserManager.GetRoleRanksAsync(appUser);

                if (appRoleRanks.All(r => r.IsStaffOrBackend()))
                {
                    if (Verbose)
                        _logger.LogInformation("Deleting all roles from Personnel User with id {UserId}...", appUser.Id);
                    await _appUserManager.RemoveFromRolesAsync(appUser, appRoleRanks.Select(rr => rr.ToNormalizedString()));

                    if (Verbose)
                        _logger.LogInformation("Assigning \"None\" role to Personnel User with id {UserId}...", appUser.Id);
                    await _appUserManager.AddToRoleAsync(appUser, RoleRank.None.ToNormalizedString());

                    continue;
                }

                if (Verbose)
                {
                    _logger.LogWarning("Personnel user with id {AppUserId} obviation is skipped because they have client roles too.", appUser.Id);
                    _logger.LogInformation("Deleting non-client roles from personnel user with id {AppUserId}...", appUser.Id);
                }

                var rolesToRemove = appRoleRanks.Where(r => !r.IsClient()).Select(r => r.ToNormalizedString());
                await _appUserManager.RemoveFromRolesAsync(appUser, rolesToRemove);
            }
        }
    }
}
