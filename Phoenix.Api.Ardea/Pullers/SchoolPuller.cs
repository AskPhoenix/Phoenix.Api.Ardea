using Microsoft.EntityFrameworkCore;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using Phoenix.DataHandle.WordPress;
using Phoenix.DataHandle.WordPress.Models;
using Phoenix.DataHandle.WordPress.Models.Uniques;
using Phoenix.DataHandle.WordPress.Wrappers;
using WordPressPCL.Models;

namespace Phoenix.Api.Ardea.Pullers
{
    public class SchoolPuller : WPPuller<School>
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
            Logger.LogInformation("-----------------------------------------------------------------");
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
                        Logger.LogInformation("Adding school {SchoolUq}", schoolAcf.SchoolUnique.ToString());

                    school = schoolAcf.ToContext();
                    school.SchoolSettings = schoolAcf.ExtractSchoolSettings();

                    schoolRepository.Create(school);
                }
                else
                {
                    if (Verbose)
                        Logger.LogInformation("Updating school {SchoolUq}", schoolAcf.SchoolUnique.ToString());

                    schoolRepository.Update(school, schoolAcf.ToContext(), schoolAcf.ExtractSchoolSettings());
                    schoolRepository.Restore(school);
                }

                SchoolUqsDict.Add(school.Id, schoolAcf.SchoolUnique);
            }
            
            Logger.LogInformation("Schools synchronization finished");
            Logger.LogInformation("-----------------------------------------------------------------");

            return PulledIds = SchoolUqsDict.Keys.ToList();
        }

        public override async Task<List<int>> ObviateAsync(List<int> toKeep)
        {
            Logger.LogInformation("-----------------------------------------------------------------");
            Logger.LogInformation("Schools obviation started");

            var toObviate = await schoolRepository.Find()
                .Where(s => !toKeep.Contains(s.Id))
                .ToListAsync();

            ObviatedIds = ObviateGroup(toObviate, schoolRepository);

            Logger.LogInformation("Schools obviation finished");
            Logger.LogInformation("-----------------------------------------------------------------");

            return ObviatedIds;
        }

        public override async Task PutAsync()
        {
            _ = await PullAsync();

            if (SpecificSchoolOnly)
            {
                Logger.LogWarning("Schools obviation skipped due to specific school mode");
                return;
            }
            
            _ = ObviateAsync();
        }
    }
}
