using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Models;
using Phoenix.DataHandle.DataEntry.Models.Uniques;
using Phoenix.DataHandle.Identity;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Main.Types;
using Phoenix.DataHandle.Repositories;
using User = Phoenix.DataHandle.Main.Models.User;

namespace Phoenix.Api.Ardea.Pullers
{
    public class ClientPuller : UserPuller
    {
        public override PostCategory PostCategory => PostCategory.Client;

        public ClientPuller(
            Dictionary<int, SchoolUnique> schoolUqsDict,
            Dictionary<int, CourseUnique> courseUqsDict,
            ApplicationUserManager appUserManager,
            PhoenixContext phoenixContext,
            ILogger logger,
            bool verbose = true)
            : base(schoolUqsDict, courseUqsDict, appUserManager, phoenixContext, logger, verbose)
        {
        }

        public override async Task<List<int>> PullAsync()
        {
            _logger.LogInformation("-----------------------------------------------------------------");
            _logger.LogInformation("Clients synchronization started.");

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                var posts = await this.GetPostsForSchoolAsync(schoolUqPair.Value);

                _logger.LogInformation("{ClientsNumber} Clients found for School \"{SchoolUq}\".",
                    posts.Count(), schoolUqPair.Value);

                var school = await _schoolRepository.FindUniqueAsync(schoolUqPair.Value);

                foreach (var clientPost in posts)
                {
                    try
                    {
                        var studentAcf = await WPClientWrapper.GetClientAcfAsync(clientPost);
                        var parentsAcf = new ClientAcf?[2] { studentAcf.Parent1, studentAcf.Parent2 };

                        var appParents = new ApplicationUser?[2];
                        var parents = new User?[2];

                        // Parents
                        for (int i = 0; i < 2; i++)
                        {
                            var parentAcf = parentsAcf[i];
                            if (parentAcf is null)
                                continue;

                            var appParent = await _appUserManager.FindByPhoneNumberAsync(parentAcf.PhoneString);
                            appParent = await this.PutAppUserAsync(appParent, parentAcf, schoolUqPair.Value);
                            if (appParent is null)
                                continue;

                            var parent = await _userRepository.FindPrimaryAsync(appParent.Id);
                            parent = await this.PutUserAsync(parent, parentAcf, appParent.Id);

                            await this.PutUserToRoleAsync(appParent, parentAcf.Role);
                            await this.PutUserToSchoolAsync(parent, school!);
                            await this.PutUserToCoursesAsync(parent, parentAcf, school!);

                            PulledIds.Add(appParent.Id);

                            appParents[i] = appParent;
                            parents[i] = parent;
                        }

                        // Student
                        ApplicationUser? appStudent = null;
                        User? student = null;

                        if (studentAcf.IsSelfDetermined)
                        {
                            appStudent = await _appUserManager.FindByPhoneNumberAsync(studentAcf.PhoneString);
                            appStudent = await this.PutAppUserAsync(appStudent, studentAcf, schoolUqPair.Value);
                            if (appStudent is null)
                                continue;

                            student = (await _userRepository.FindPrimaryAsync(appStudent.Id))!;
                        }
                        else
                        {
                            for (int i = 0; i < 2; i++)
                            {
                                student = parents[i]?.Children
                                    .SingleOrDefault(c => string.Equals(c.FullName, studentAcf.FullName, StringComparison.OrdinalIgnoreCase));

                                if (student is not null)
                                    break;
                            }

                            if (student is null || student.DependenceOrder == 0)
                            {
                                int p = parents[0] is null ? 1 : 0;
                                studentAcf.DependenceOrder = parents[p]!.Children
                                    .Select(c => c.DependenceOrder)
                                    .DefaultIfEmpty(0)
                                    .Max() + 1;
                            }
                            else
                            {
                                studentAcf.DependenceOrder = student.DependenceOrder;
                            }

                            if (student is not null)
                                appStudent = await _appUserManager.FindByIdAsync(student.AspNetUserId.ToString());

                            appStudent = await this.PutAppUserAsync(appStudent, studentAcf, schoolUqPair.Value);
                            if (appStudent is null)
                                continue;
                        }

                        student = await this.PutUserAsync(student, studentAcf, appStudent.Id);

                        await this.PutUserToRoleAsync(appStudent, studentAcf.Role);
                        await this.PutUserToSchoolAsync(student, school!);
                        await this.PutUserToCoursesAsync(student, studentAcf, school!);

                        PulledIds.Add(appStudent.Id);

                        // Link Parents with the student
                        for (int i = 0; i < 2; i++)
                        {
                            if (parents[i] is null)
                                continue;

                            if (Verbose)
                                _logger.LogInformation("Linking student with their parent {i}...", i);

                            var children = parents[i]!.Children;

                            if (!children.Contains(student))
                                children.Add(student);

                            await _userRepository.UpdateAsync(parents[i]!);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex.Message);
                        _logger.LogWarning("Skipping post...");
                        continue;
                    }
                }

                _logger.LogInformation("Students & Parents synchronization finished.");
                _logger.LogInformation("-----------------------------------------------------------------");
            }

            return PulledIds = PulledIds.Distinct().ToList();
        }

        public override async Task<List<int>> ObviateAsync(List<int> toKeep)
        {
            IEnumerable<User> findClientsForSchool(int schoolId)
            {
                var school = Task.Run(() => _schoolRepository.FindPrimaryAsync(schoolId)).Result;
                if (school is null)
                    return Enumerable.Empty<User>();

                var users = school.Users.ToList();
                var clients = new List<User>();

                foreach (var user in users)
                {
                    var appUser = _appUserManager.FindByIdAsync(user.AspNetUserId.ToString()).Result;
                    var roles = _appUserManager.GetRoleRanksAsync(appUser).Result;

                    if (roles.Any(r => r.IsClient()))
                        clients.Add(user);
                }

                return clients;
            }

            return ObviatedIds = await ObviateAllPerSchoolAsync(findClientsForSchool, _userRepository, toKeep);
        }

        protected override async Task<List<int>> ObviateGroupAsync(IList<User> toObviate, ObviableRepository<User> userRepository)
        {
            var obviatedIds = await base.ObviateGroupAsync(toObviate, userRepository);
            if (obviatedIds.Any())
                await this.DeassignRolesFromObviatedAsync(toObviate, RoleHierarchy.ClientRolesBase);

            return obviatedIds;
        }
    }
}
