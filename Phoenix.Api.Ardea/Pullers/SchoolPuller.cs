using Microsoft.EntityFrameworkCore;
using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Models.Uniques;
using Phoenix.DataHandle.Main.Entities;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using WordPressPCL.Models;

namespace Phoenix.Api.Ardea.Pullers
{
    public class SchoolPuller : WPPuller<School>
    {
        private readonly SchoolRepository schoolRepository;

        public SchoolUnique? SpecificSchoolUq { get; }
        public bool SpecificSchoolOnly { get; }

        public override PostCategory PostCategory => PostCategory.SchoolInformation;

        public SchoolPuller(PhoenixContext phoenixContext, ILogger logger, 
            SchoolUnique? specificSchoolUq = null, bool verbose = true)
            : base(logger, verbose)
        {
            this.schoolRepository = new(phoenixContext);

            this.SpecificSchoolUq = specificSchoolUq;
            this.SpecificSchoolOnly = specificSchoolUq != null;
        }

        public override async Task<List<int>> PullAsync()
        {
            Logger.LogInformation("-----------------------------------------------------------------");
            Logger.LogInformation("Schools synchronization started.");

            IEnumerable<Post> schoolPosts = await WPClientWrapper.GetPostsAsync(this.PostCategory);
            if (SpecificSchoolOnly)
                schoolPosts = schoolPosts.FilterPostsForSchool(SpecificSchoolUq!);
            else
                Logger.LogInformation("{SchoolsNumber} Schools found.", schoolPosts.Count());

            var toCreate = new List<School>();
            var toUpdate = new List<School>();
            var toUpdateFrom = new List<School>();

            foreach (var schoolPost in schoolPosts)
            {
                var schoolAcf = await WPClientWrapper.GetSchoolAcfAsync(schoolPost);
                var schoolUq = schoolAcf.GetSchoolUnique();
                var school = await schoolRepository.FindUniqueAsync(schoolAcf);

                if (school is null)
                {
                    if (Verbose)
                        Logger.LogInformation("School {SchoolUq} to be created.", schoolUq.ToString());

                    // TODO: To check
                    school = (School)(ISchool)schoolAcf;
                    //school.SchoolSettings = schoolAcf.ExtractSchoolSettings();

                    toCreate.Add(school);
                }
                else
                {
                    if (Verbose)
                        Logger.LogInformation("School {SchoolUq} to be updated.", schoolUq.ToString());

                    toUpdate.Add(school);
                    toUpdateFrom.Add((School)(ISchool)schoolAcf);
                }
            }

            Logger.LogInformation("Creating {ToCreateNum} schools...", toCreate.Count);
            var created = await schoolRepository.CreateRangeAsync(toCreate);
            Logger.LogInformation("{CreatedNum}/{ToCreateNum} schools created successfully.",
                created.Count(), toCreate.Count);

            Logger.LogInformation("Updating {ToUpdateNum} schools...", toUpdate.Count);
            var updated = await schoolRepository.UpdateRangeAsync(toUpdate, toUpdateFrom);
            var restored = await schoolRepository.RestoreRangeAsync(toUpdate);
            Logger.LogInformation("{UpdatedNum}/{ToUpdateNum} schools updated successfully.",
                updated.Count() + restored.Count(), toUpdate.Count);

            Logger.LogInformation("Schools synchronization finished.");
            Logger.LogInformation("-----------------------------------------------------------------");

            foreach (var school in created.Concat(updated))
                SchoolUqsDict.Add(school.Id, new SchoolUnique(school.Code));

            PulledIds.AddRange(SchoolUqsDict.Keys);

            return PulledIds;
        }

        public override async Task<List<int>> ObviateAsync(List<int> toKeep)
        {
            Logger.LogInformation("-----------------------------------------------------------------");
            Logger.LogInformation("Schools obviation started.");

            var toObviate = await schoolRepository.Find()
                .Where(s => !toKeep.Contains(s.Id))
                .ToListAsync();

            ObviatedIds = await ObviateGroupAsync(toObviate, schoolRepository);

            Logger.LogInformation("Schools obviation finished.");
            Logger.LogInformation("-----------------------------------------------------------------");

            return ObviatedIds;
        }

        public override async Task PutAsync()
        {
            _ = await PullAsync();

            if (SpecificSchoolOnly)
            {
                Logger.LogWarning("Schools obviation skipped due to specific school mode.");
                return;
            }
            
            _ = await ObviateAsync();
        }
    }
}
