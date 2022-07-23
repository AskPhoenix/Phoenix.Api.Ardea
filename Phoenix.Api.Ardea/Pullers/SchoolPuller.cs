using Microsoft.EntityFrameworkCore;
using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Types;
using Phoenix.DataHandle.DataEntry.Types.Uniques;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Main.Models.Extensions;
using WordPressPCL.Models;

namespace Phoenix.Api.Ardea.Pullers
{
    public class SchoolPuller : WPPuller<School>
    {
        public SchoolUnique? SpecificSchoolUq { get; protected set; }
        public bool SpecificSchoolOnly => this.SpecificSchoolUq is not null;

        public override PostCategory PostCategory => PostCategory.SchoolInformation;

        public SchoolPuller(
            SchoolUnique? specificSchoolUq,
            PhoenixContext phoenixContext,
            ILogger logger,
            bool verbose = true)
            : base(phoenixContext, logger, verbose)
        {
            this.SpecificSchoolUq = specificSchoolUq;
        }

        public override async Task<List<int>> PullAsync()
        {
            _logger.LogInformation("-----------------------------------------------------------------");
            _logger.LogInformation("Schools synchronization started.");

            IEnumerable<Post> schoolPosts;
            if (SpecificSchoolOnly)
                schoolPosts = await this.GetPostsForSchoolAsync(SpecificSchoolUq!);
            else
            {
                schoolPosts = await WPClientWrapper.GetPostsAsync(this.PostCategory);
                _logger.LogInformation("{SchoolsNumber} Schools found.", schoolPosts.Count());
            }

            var toCreate = new List<School>();
            var toUpdate = new List<School>();

            foreach (var schoolPost in schoolPosts)
            {
                try
                {
                    var schoolAcf = await WPClientWrapper.GetSchoolAcfAsync(schoolPost);
                    var schoolUq = schoolAcf.GetSchoolUnique();
                    var school = await _schoolRepository.FindUniqueAsync(schoolAcf);

                    if (school is null)
                    {
                        if (Verbose)
                            _logger.LogInformation("School \"{SchoolUq}\" to be created.", schoolUq.ToString());

                        school = schoolAcf.ToSchool();
                        toCreate.Add(school);
                    }
                    else
                    {
                        if (Verbose)
                            _logger.LogInformation("School \"{SchoolUq}\" to be updated.", schoolUq.ToString());

                        toUpdate.Add(schoolAcf.ToSchool(school));
                    }
                }
                catch(Exception ex)
                {
                    _logger.LogError(ex.Message);
                    _logger.LogWarning("Skipping post...");
                    continue;
                }
            }

            var created = new List<School>();
            if (toCreate.Any())
            {
                _logger.LogInformation("Creating {ToCreateNum} schools...", toCreate.Count);
                created = (await _schoolRepository.CreateRangeAsync(toCreate)).ToList();
                _logger.LogInformation("{CreatedNum}/{ToCreateNum} schools created successfully.",
                    created.Count(), toCreate.Count);
            }
            else
                _logger.LogInformation("No schools to create.");

            var updated = new List<School>();
            if (toUpdate.Any())
            {
                _logger.LogInformation("Updating {ToUpdateNum} schools...", toUpdate.Count);
                updated = (await _schoolRepository.UpdateRangeAsync(toUpdate)).ToList();
                await _schoolRepository.RestoreRangeAsync(toUpdate);
                _logger.LogInformation("{UpdatedNum}/{ToUpdateNum} schools updated successfully.",
                    updated.Count(), toUpdate.Count);
            }
            else
                _logger.LogInformation("No schools to update.");

            _logger.LogInformation("Schools synchronization finished.");
            _logger.LogInformation("-----------------------------------------------------------------");

            foreach (var school in created.Concat(updated))
                SchoolUqsDict.Add(school.Id, new SchoolUnique(school.Code));

            PulledIds.AddRange(SchoolUqsDict.Keys);

            return PulledIds;
        }

        public override async Task<List<int>> ObviateAsync(List<int> toKeep)
        {
            _logger.LogInformation("-----------------------------------------------------------------");
            _logger.LogInformation("Schools obviation started.");

            var toObviate = await _schoolRepository.Find()
                .Where(s => !toKeep.Contains(s.Id))
                .Where(s => (s as IObviableModelEntity).IsObviated)
                .ToListAsync();

            ObviatedIds = await ObviateGroupAsync(toObviate, _schoolRepository);

            _logger.LogInformation("Schools obviation finished.");
            _logger.LogInformation("-----------------------------------------------------------------");

            return ObviatedIds;
        }

        public override async Task PutAsync()
        {
            _ = await PullAsync();

            if (SpecificSchoolOnly)
            {
                _logger.LogWarning("Schools obviation skipped due to specific school mode.");
                return;
            }
            
            _ = await ObviateAsync();
        }
    }
}
