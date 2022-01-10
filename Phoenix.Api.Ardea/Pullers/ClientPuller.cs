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
    public class ClientPuller : WPPuller<AspNetUsers>
    {
        protected readonly AspNetUserRepository aspNetUserRepository;
        protected readonly SchoolRepository schoolRepository;

        public ClientPuller(Dictionary<int, SchoolUnique> schoolUqsDict, Dictionary<int, CourseUnique> courseUqsDict,
            PhoenixContext phoenixContext, ILogger logger, bool verbose = true)
            : base(schoolUqsDict, courseUqsDict, logger, verbose)
        {
            this.aspNetUserRepository = new(phoenixContext);
            this.aspNetUserRepository.Include(u => u.User);
            this.aspNetUserRepository.Include(u => u.ParenthoodChild);
            this.aspNetUserRepository.Include(u => u.ParenthoodParent);
            this.schoolRepository = new(phoenixContext);
        }

        public override int CategoryId => PostCategoryWrapper.GetCategoryId(PostCategory.Client);

        public override async Task<List<int>> PullAsync()
        {
            Logger.LogInformation("-----------------------------------------------------------------");
            Logger.LogInformation("Students & Parents synchronization started");

            IEnumerable<Post> clientPosts = await WordPressClientWrapper.GetPostsAsync(CategoryId);
            IEnumerable<Post> filteredPosts;

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                filteredPosts = clientPosts.FilterPostsForSchool(schoolUqPair.Value);

                Logger.LogInformation("{ClientsNumber} Clients found for School \"{SchoolUq}\"",
                    filteredPosts.Count(), schoolUqPair.Value.ToString());

                foreach (var clientPost in filteredPosts)
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
                            aspNetUserRepository.Restore(parent);

                            if (!aspNetUserRepository.HasRole(parent, Role.Parent))
                                aspNetUserRepository.LinkRole(parent, Role.Parent);
                        }

                        parentIds.Add(parent.Id);
                    }

                    PulledIds.AddRange(parentIds);

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
                            Logger.LogInformation("Updating Student with {ParentStr}phone number: {PhoneNumber}",
                                (clientAcf.IsSelfDetermined) ? "" : "parent ", clientAcf.TopPhoneNumber);

                        var updatedUser = clientAcf.ExtractUser();
                        var aspNetUserFrom = clientAcf.ToContext();
                        aspNetUserFrom.UserName = ClientACF.GetUserName(updatedUser, schoolUqPair.Key, clientAcf.TopPhoneNumber);
                        aspNetUserFrom.NormalizedUserName = aspNetUserFrom.UserName.ToUpperInvariant();

                        aspNetUserRepository.Update(student, aspNetUserFrom, updatedUser);
                        aspNetUserRepository.Restore(student);
                        
                        if (!aspNetUserRepository.HasRole(student, Role.Student))
                            this.aspNetUserRepository.LinkRole(student, Role.Student);
                    }

                    PulledIds.Add(student.Id);

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
                Logger.LogInformation("-----------------------------------------------------------------");
            }

            return PulledIds = PulledIds.Distinct().ToList();
        }

        public override async Task<List<int>> ObviateAsync(List<int> toKeep)
        {
            return ObviatedIds = await ObviateAllPerSchoolAsync(schoolRepository.FindClients, aspNetUserRepository, toKeep);
        }

        protected override int? ObviateUnit(AspNetUsers user, ObviableRepository<AspNetUsers> repository)
        {
            var roles = aspNetUserRepository.FindRoles(user).Select(r => r.Type);

            if (roles.All(r => r.IsClient()))
            {
                if (Verbose)
                    Logger.LogInformation("Deleting all roles from client user with id {UserId}", user.Id);
                aspNetUserRepository.DeleteRoles(user);

                if (Verbose)
                    Logger.LogInformation("Assigning \"None\" role to client user with id {UserId}", user.Id);
                aspNetUserRepository.LinkRole(user, Role.None);

                return base.ObviateUnit(user, repository);
            }

            Logger.LogWarning("Client user with id {UserId} obviation is skipped because they have non-client roles too", user.Id);

            if (Verbose)
                Logger.LogInformation("Deleting client roles from client user with id {UserId}", user.Id);
            aspNetUserRepository.DeleteRoles(user, roles.Where(r => !r.IsClient()).ToList());

            return null;
        }
    }
}
