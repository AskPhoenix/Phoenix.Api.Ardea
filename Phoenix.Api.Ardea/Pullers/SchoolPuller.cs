using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using Phoenix.DataHandle.WordPress;
using Phoenix.DataHandle.WordPress.Models;
using Phoenix.DataHandle.WordPress.Models.Uniques;
using Phoenix.DataHandle.WordPress.Wrappers;
using WordPressPCL.Models;

namespace Phoenix.Api.Ardea.Pullers
{
    public class SchoolPuller : WPPuller
    {
        private readonly SchoolRepository schoolRepository;

        public SchoolUnique? SpecificSchoolUq { get; }
        public bool SpecificSchoolOnly { get; }

        public SchoolPuller(PhoenixContext phoenixContext, ILogger logger, 
            SchoolUnique? specificSchoolUq = null, bool verbose = true)
            : base(logger, verbose)
        {
            this.schoolRepository = new(phoenixContext);
            this.schoolRepository.Include(s => s.SchoolSettings);

            this.SpecificSchoolUq = specificSchoolUq;
            this.SpecificSchoolOnly = specificSchoolUq != null;
        }

        public override int CategoryId => PostCategoryWrapper.GetCategoryId(PostCategory.SchoolInformation);

        public override async Task<List<int>> PullAsync()
        {
            Logger.LogInformation("-------------------------------");
            Logger.LogInformation("Schools synchronization started");

            IEnumerable<Post> schoolPosts = await WordPressClientWrapper.GetPostsAsync(CategoryId);
            if (SpecificSchoolOnly)
                schoolPosts = schoolPosts.FilterPostsForSchool(SpecificSchoolUq!);

            Logger.LogInformation("{SchoolsNumber} Schools found", schoolPosts.Count());

            foreach (var schoolPost in schoolPosts)
            {
                SchoolACF schoolAcf = (SchoolACF)(await WordPressClientWrapper.GetAcfAsync<SchoolACF>(schoolPost.Id)).WithTitleCase();

                var school = await schoolRepository.Find(checkUnique: schoolAcf.MatchesUnique);
                if (school is null)
                {
                    if (Verbose)
                        Logger.LogInformation("Adding School: {SchoolUq}", schoolAcf.SchoolUnique.ToString());

                    school = schoolAcf.ToContext();
                    school.SchoolSettings = schoolAcf.ExtractSchoolSettings();

                    schoolRepository.Create(school);
                }
                else
                {
                    if (Verbose)
                        Logger.LogInformation("Updating School: {SchoolUq}", schoolAcf.SchoolUnique.ToString());

                    schoolRepository.Update(school, schoolAcf.ToContext(), schoolAcf.ExtractSchoolSettings());
                    schoolRepository.Restore(school);
                }

                SchoolUqsDict.Add(school.Id, schoolAcf.SchoolUnique);
            }
            
            PulledIds = SchoolUqsDict.Keys.ToList();

            Logger.LogInformation("Schools synchronization finished");
            Logger.LogInformation("--------------------------------");

            return PulledIds;
        }

        public override List<int> Obviate()
        {
            if (SpecificSchoolOnly)
            {
                Logger.LogWarning("Schools obviation skipped due to specific school mode");
                return new();
            }

            Logger.LogInformation("------------------------");
            Logger.LogInformation("Schools obviation started");

            var toObviate = schoolRepository.Find().Where(s => !SchoolUqsDict.ContainsKey(s.Id));

            ObviatedIds = ObviateGroup<School>(toObviate, schoolRepository);

            Logger.LogInformation("Schools obviation finished");
            Logger.LogInformation("-------------------------");

            return ObviatedIds;
        }
    }
}
