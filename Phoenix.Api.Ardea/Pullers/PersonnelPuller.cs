using Phoenix.DataHandle.Main;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using Phoenix.DataHandle.WordPress;
using Phoenix.DataHandle.WordPress.Models;
using Phoenix.DataHandle.WordPress.Models.Uniques;
using Phoenix.DataHandle.WordPress.Wrappers;
using WordPressPCL.Models;

namespace Phoenix.Api.Ardea.Pullers
{
    public class PersonnelPuller : WPPuller<AspNetUsers>
    {
        protected readonly AspNetUserRepository aspNetUserRepository;
        protected readonly SchoolRepository schoolRepository;

        public PersonnelPuller(Dictionary<int, SchoolUnique> schoolUqsDict, Dictionary<int, CourseUnique> courseUqsDict,
            PhoenixContext phoenixContext, ILogger logger, bool verbose = true)
            : base(schoolUqsDict, courseUqsDict, logger, verbose)
        {
            this.aspNetUserRepository = new(phoenixContext);
            this.aspNetUserRepository.Include(u => u.User);
            this.schoolRepository = new(phoenixContext);
        }

        public override int CategoryId => PostCategoryWrapper.GetCategoryId(PostCategory.Personnel);

        public override async Task<List<int>> PullAsync()
        {
            Logger.LogInformation("-----------------------------------------------------------------");
            Logger.LogInformation("Personnel synchronization started");

            IEnumerable<Post> personnelPosts = await WordPressClientWrapper.GetPostsAsync(CategoryId);
            IEnumerable<Post> filteredPosts;

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                filteredPosts = personnelPosts.FilterPostsForSchool(schoolUqPair.Value);

                Logger.LogInformation("{PersonnelNumber} Staff members found for School \"{SchoolUq}\"",
                    filteredPosts.Count(), schoolUqPair.Value.ToString());

                foreach (var personnelPost in filteredPosts)
                {
                    var personnelAcf = (PersonnelACF)(await WordPressClientWrapper.GetAcfAsync<PersonnelACF>(personnelPost.Id)).WithTitleCase();
                    personnelAcf.SchoolUnique = schoolUqPair.Value;

                    var aspNetUser = await aspNetUserRepository.Find(checkUnique: personnelAcf.MatchesUnique);
                    if (aspNetUser is null)
                    {
                        if (Verbose)
                            Logger.LogInformation("Adding Personnel User with phone number: {UserPhone}",
                                personnelAcf.PhoneString);

                        aspNetUser = personnelAcf.ToContext();
                        aspNetUser.User = personnelAcf.ExtractUser();
                        aspNetUser.UserName = PersonnelACF.GetUserName(aspNetUser.User, schoolUqPair.Key, aspNetUser.PhoneNumber);
                        aspNetUser.NormalizedUserName = aspNetUser.UserName.ToUpperInvariant();

                        aspNetUserRepository.Create(aspNetUser);

                        aspNetUserRepository.LinkSchool(aspNetUser, schoolUqPair.Key);
                    }
                    else
                    {
                        if (Verbose)
                            Logger.LogInformation("Updating Personnel User with phone number: {UserPhone}",
                                aspNetUser.PhoneNumber);

                        var userFrom = personnelAcf.ExtractUser();
                        var aspNetUserFrom = personnelAcf.ToContext();
                        aspNetUserFrom.UserName = PersonnelACF.GetUserName(userFrom, schoolUqPair.Key, aspNetUser.PhoneNumber);
                        aspNetUserFrom.NormalizedUserName = aspNetUserFrom.UserName.ToUpperInvariant();

                        aspNetUserRepository.Update(aspNetUser, aspNetUserFrom, userFrom);
                        aspNetUserRepository.Restore(aspNetUser);
                    }

                    PulledIds.Add(aspNetUser.Id);

                    if (Verbose)
                        Logger.LogInformation("Linking Personnel User with phone number {UserPhone} with their roles",
                            aspNetUser.PhoneNumber);

                    if (!aspNetUserRepository.HasRole(aspNetUser, personnelAcf.RoleType))
                        aspNetUserRepository.LinkRole(aspNetUser, personnelAcf.RoleType);

                    // Delete any other roles the user might have
                    // The only possible scenario where 2 roles are allowed is: a non-client role + parent
                    aspNetUserRepository.DeleteRoles(aspNetUser, new Role[2] { personnelAcf.RoleType, Role.Parent });

                    if (Verbose)
                        Logger.LogInformation("Linking Personnel User with phone number {UserPhone} with their courses",
                            aspNetUser.PhoneNumber);

                    var userCourseCodes = personnelAcf.ExtractCourseCodes();
                    var userCourseUqs = userCourseCodes.Select(c => new CourseUnique(schoolUqPair.Value, c));
                    var userCourseIds = CourseUqsDict.Where(kv => userCourseUqs.Contains(kv.Value)).Select(kv => kv.Key);

                    aspNetUserRepository.LinkCourses(aspNetUser, userCourseIds.ToList(), deleteAdditionalLinks: true);
                }
            }

            Logger.LogInformation("Personnel synchronization finished");
            Logger.LogInformation("-----------------------------------------------------------------");

            return PulledIds = PulledIds.Distinct().ToList();
        }

        public override async Task<List<int>> ObviateAsync(List<int> toKeep)
        {
            return ObviatedIds = await ObviateAllPerSchoolAsync(schoolRepository.FindPersonnel, aspNetUserRepository, toKeep);
        }

        protected override int? ObviateUnit(AspNetUsers user, ObviableRepository<AspNetUsers> repository)
        {
            var roles = aspNetUserRepository.FindRoles(user).Select(r => r.Type);

            if (roles.All(r => r.IsPersonnel()))
            {
                if (Verbose)
                    Logger.LogInformation("Deleting all roles from personnel user with id {UserId}", user.Id);
                aspNetUserRepository.DeleteRoles(user);

                if (Verbose)
                    Logger.LogInformation("Assigning \"None\" role to personnel user with id {UserId}", user.Id);
                aspNetUserRepository.LinkRole(user, Role.None);

                return base.ObviateUnit(user, repository);
            }

            Logger.LogWarning("Personnel user with id {UserId} obviation is skipped because they have client roles too", user.Id);
            
            if (Verbose)
                Logger.LogInformation("Deleting non-client roles from personnel user with id {UserId}", user.Id);
            aspNetUserRepository.DeleteRoles(user, roles.Where(r => r.IsClient()).ToList());

            return null;
        }
    }
}
