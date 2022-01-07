using Phoenix.DataHandle.Main;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using Phoenix.DataHandle.WordPress;
using Phoenix.DataHandle.WordPress.Models;
using Phoenix.DataHandle.WordPress.Models.Uniques;
using Phoenix.DataHandle.WordPress.Utilities;
using Phoenix.DataHandle.WordPress.Wrappers;
using WordPressPCL.Models;

namespace Phoenix.Api.Ardea.Pullers
{
    public class ClientPuller : WPPuller
    {
        private readonly AspNetUserRepository aspNetUserRepository;

        public ClientPuller(Dictionary<int, SchoolUnique> schoolUqsDict, Dictionary<int, CourseUnique> courseUqsDict,
            PhoenixContext phoenixContext, ILogger logger, SchoolUnique? specificSchoolUq = null, bool verbose = true) 
            : base(schoolUqsDict, courseUqsDict, logger, specificSchoolUq, verbose)
        {
            this.aspNetUserRepository = new(phoenixContext);
            this.aspNetUserRepository.Include(u => u.User);
            this.aspNetUserRepository.Include(u => u.ParenthoodChild);
            this.aspNetUserRepository.Include(u => u.ParenthoodParent);
        }

        public override int CategoryId => PostCategoryWrapper.GetCategoryId(PostCategory.Client);

        public override async Task<int[]> PullAsync()
        {
            Logger.LogInformation("------------------------------------------");
            Logger.LogInformation("Students & Parents synchronization started");

            IEnumerable<Post> clientPosts = await WordPressClientWrapper.GetPostsAsync(CategoryId);
            IEnumerable<Post> filteredPosts;

            int P = clientPosts.Count();
            int[] updatedIds = new int[P];

            int p = 0;
            foreach (var schoolUqPair in SchoolUqsDict)
            {
                foreach (var clientPost in clientPosts)
                {
                    var clientAcf = (ClientACF)(await WordPressClientWrapper.GetAcfAsync<ClientACF>(clientPost.Id)).WithTitleCase();
                    clientAcf.SchoolUnique = schoolUqPair.Value;

                    var parents = clientAcf.ExtractParents();
                    var parentUsers = clientAcf.ExtractParentUsers();

                    var parentIds = new List<int>(parents.Count);
                    for (int i = 0; i < parents.Count; i++)
                    {
                        parents[i].UserName = ClientACF.GetUserName(parentUsers[i], schoolUqPair.Key, parents[i].PhoneNumber);
                        parents[i].NormalizedUserName = parents[i].UserName.ToUpperInvariant();

                        string parentPhoneString = i == 0 ? clientAcf.Parent1PhoneString : clientAcf.Parent2PhoneString;
                        AspNetUsers? parent = aspNetUserRepository.Find().
                                SingleOrDefault(u => u.User.IsSelfDetermined && u.PhoneNumber == parentPhoneString);

                        if (parent is null)
                        {
                            if (Verbose)
                                Logger.LogInformation("Adding Parent {ParentNumber} with phone number {ParentPhone}.",
                                    i+1, parentPhoneString);

                            parent = parents[i];
                            parent.User = parentUsers[i];

                            aspNetUserRepository.Create(parent);
                            aspNetUserRepository.LinkRole(parent, Role.Parent);

                            if (Verbose)
                                Logger.LogInformation("Linking Parent {ParentNumber} with phone number {ParentPhone}" +
                                    " with school \"{SchoolUq}\"", i+1, parentPhoneString, schoolUqPair.Value);
                            
                            aspNetUserRepository.LinkSchool(parent, schoolUqPair.Key);
                        }
                        else
                        {
                            if (Verbose)
                                Logger.LogInformation("Updating Parent {ParentNumber} with phone number {ParentPhone}.",
                                    i+1, parentPhoneString);

                            aspNetUserRepository.Update(parent, parents[i], parentUsers[i]);
                            if (!aspNetUserRepository.HasRole(parent, Role.Parent))
                                aspNetUserRepository.LinkRole(parent, Role.Parent);
                        }

                        parentIds.Add(parent.Id);
                    }

                    AspNetUsers student = null!;
                    if (clientAcf.IsSelfDetermined)
                        student = await aspNetUserRepository.Find(checkUnique: clientAcf.MatchesUnique);
                    else
                    {
                        if (!clientAcf.HasParent1 && !clientAcf.HasParent2)
                            Logger.LogError("Non self determined users must have at least one parent. " +
                                "WordPress Post {PostTitle} was skipped.", clientPost.GetTitle());

                        student = aspNetUserRepository.FindChild(parentIds.First(), 
                            clientAcf.StudentFirstName, clientAcf.StudentLastName);
                    }

                    if (student is null)
                    {
                        if (Verbose)
                            Logger.LogInformation("Adding Student with {ParentStr}phone number: {PhoneNumber}",
                                (clientAcf.IsSelfDetermined) ? "" : "parent ", clientAcf.TopPhoneNumber);

                        student = clientAcf.ToContext();
                        student.User = clientAcf.ExtractUser();
                        student.UserName = ClientACF.GetUserName(student.User, schoolUqPair.Key, clientAcf.TopPhoneNumber);
                        student.NormalizedUserName = student.UserName.ToUpperInvariant();

                        aspNetUserRepository.Create(student);
                        
                        aspNetUserRepository.LinkSchool(student, schoolUqPair.Key);
                        aspNetUserRepository.LinkRole(student, Role.Student);
                    }
                    else
                    {
                        if (Verbose)
                            Logger.LogInformation("Adding Student with {ParentStr}phone number: {PhoneNumber}",
                                (clientAcf.IsSelfDetermined) ? "" : "parent ", clientAcf.TopPhoneNumber);

                        var updatedUser = clientAcf.ExtractUser();
                        var aspNetUserFrom = clientAcf.ToContext();
                        aspNetUserFrom.UserName = ClientACF.GetUserName(updatedUser, schoolUqPair.Key, clientAcf.TopPhoneNumber);
                        aspNetUserFrom.NormalizedUserName = aspNetUserFrom.UserName.ToUpperInvariant();

                        this.aspNetUserRepository.Update(student, aspNetUserFrom, updatedUser);
                        
                        if (!aspNetUserRepository.HasRole(student, Role.Student))
                            this.aspNetUserRepository.LinkRole(student, Role.Student);
                    }

                    updatedIds[p++] = student.Id;

                    foreach (int parId in parentIds)
                    {
                        if (aspNetUserRepository.FindChild(parId, clientAcf.StudentFirstName, clientAcf.StudentLastName) is null)
                        {
                            if (Verbose)
                                Logger.LogInformation("Linking student with parent phone number {PhoneNumber}" +
                                    " with their parents", clientAcf.TopPhoneNumber);

                            this.aspNetUserRepository.LinkParenthood(parId, student.Id);
                        }
                    }

                    if (Verbose)
                        Logger.LogInformation("Linking Student with {ParentStr}phone number {PhoneNumber} with their courses",
                                (clientAcf.IsSelfDetermined) ? "" : "parent ", clientAcf.TopPhoneNumber);

                    var userCourseCodes = clientAcf.ExtractCourseCodes();
                    var userCourseUqs = userCourseCodes.Select(c => new CourseUnique(schoolUqPair.Value, c));
                    var userCourseIds = CourseUqsDict.Where(kv => userCourseUqs.Contains(kv.Value)).Select(kv => kv.Key);

                    aspNetUserRepository.LinkCourses(student, userCourseIds.ToList(), deleteAdditionalLinks: true);
                }

                Logger.LogInformation("Students & Parents synchronization finished");
                Logger.LogInformation("-------------------------------------------");
            }

            return updatedIds;
        }

        public override Task<int[]> DeleteAsync(int[] toKeep)
        {
            throw new NotImplementedException();
        }
    }
}
