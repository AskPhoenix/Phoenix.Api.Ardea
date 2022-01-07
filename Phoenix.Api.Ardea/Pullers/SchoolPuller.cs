﻿using Phoenix.DataHandle.Main.Models;
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

        public SchoolPuller(PhoenixContext phoenixContext, ILogger logger, 
            SchoolUnique? specificSchoolUq = null, bool verbose = true)
            : base(logger, specificSchoolUq, verbose)
        {
            this.schoolRepository = new(phoenixContext);
            this.schoolRepository.Include(s => s.SchoolSettings);
        }

        public override int CategoryId => PostCategoryWrapper.GetCategoryId(PostCategory.SchoolInformation);

        public override async Task<int[]> PullAsync()
        {
            Logger.LogInformation("-------------------------------");
            Logger.LogInformation("Schools synchronization started");

            IEnumerable<Post> schoolPosts = await WordPressClientWrapper.GetPostsAsync(CategoryId);
            if (SpecificSchoolOnly)
                schoolPosts = schoolPosts.FilterPostsForSchool(SpecificSchoolUq!);

            int P = schoolPosts.Count();
            int[] updatedIds = new int[P];

            Logger.LogInformation("{SchoolsNumber} Schools found", P);

            int p = 0;
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
                }

                updatedIds[p++] = school.Id;
                SchoolUqsDict.Add(school.Id, schoolAcf.SchoolUnique);
            }

            Logger.LogInformation("Schools synchronization finished");
            Logger.LogInformation("--------------------------------");

            return updatedIds;
        }

        public override int[] Delete(int[] toKeep)
        {
            Logger.LogInformation("------------------------");
            Logger.LogInformation("Schools deletion started");

            var toDelete = schoolRepository.Find().Where(s => !toKeep.Contains(s.Id));
            var deletedIds = new int[toDelete.Count()];

            int p = 0;
            foreach (var school in toDelete)
            {
                if (school.IsDeleted)
                {
                    if (Verbose)
                        Logger.LogInformation("School with id {SchoolId} already deleted", school.Id);
                    continue;
                }

                if (Verbose)
                    Logger.LogInformation("Deleting school with id {SchoolId}", school.Id);

                school.IsDeleted = true;
                school.DeletedAt = DateTimeOffset.Now;
                schoolRepository.Update(school);

                deletedIds[p++] = school.Id;
            }

            Logger.LogInformation("Schools deletion finished");
            Logger.LogInformation("-------------------------");

            return deletedIds;
        }
    }
}
