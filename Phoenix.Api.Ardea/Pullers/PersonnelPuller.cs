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
    public class PersonnelPuller : WPPuller
    {
        private readonly AspNetUserRepository aspNetUserRepository;

        public PersonnelPuller(Dictionary<int, SchoolUnique> schoolUqsDict, Dictionary<int, CourseUnique> courseUqsDict,
            PhoenixContext phoenixContext, ILogger logger, SchoolUnique? specificSchoolUq = null, bool verbose = true) 
            : base(schoolUqsDict, courseUqsDict, logger, specificSchoolUq, verbose)
        {
            this.aspNetUserRepository = new(phoenixContext);
            this.aspNetUserRepository.Include(u => u.User);
        }

        public override int CategoryId => PostCategoryWrapper.GetCategoryId(PostCategory.Personnel);

        public override async Task<int[]> PullAsync()
        {
            Logger.LogInformation("---------------------------------");
            Logger.LogInformation("Personnel synchronization started");

            IEnumerable<Post> personnelPosts = await WordPressClientWrapper.GetPostsAsync(CategoryId);
            IEnumerable<Post> filteredPosts;

            int P = personnelPosts.Count();
            int[] updatedIds = new int[P];

            int p = 0;
            foreach (var schoolUqPair in SchoolUqsDict)
            {
                filteredPosts = personnelPosts.FilterPostsForSchool(schoolUqPair.Value);

                Logger.LogInformation("{PersonnelNumber} Staff members found for School \"{SchoolUq}\"",
                    filteredPosts.Count(), schoolUqPair.Value.ToString());

                foreach (var personnelPost in personnelPosts)
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

                        this.aspNetUserRepository.Update(aspNetUser, aspNetUserFrom, userFrom);
                    }

                    updatedIds[p++] = aspNetUser.Id;

                    if (Verbose)
                        Logger.LogInformation("Linking Personnel User with phone number {UserPhone} with their roles",
                            aspNetUser.PhoneNumber);

                    if (!aspNetUserRepository.HasRole(aspNetUser, personnelAcf.RoleType))
                        aspNetUserRepository.LinkRole(aspNetUser, personnelAcf.RoleType);

                    // Delete any other roles the user might have
                    // The only possible scenario where 2 roles are alowed is: a staff role + parent
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
            Logger.LogInformation("----------------------------------");

            return updatedIds;
        }

        public override Task<int[]> DeleteAsync(int[] toKeep)
        {
            throw new NotImplementedException();
        }
    }
}
