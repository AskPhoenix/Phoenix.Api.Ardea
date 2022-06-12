using Microsoft.EntityFrameworkCore;
using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Models.Uniques;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Main.Models.Extensions;
using Phoenix.DataHandle.Repositories;
using System.Linq.Expressions;

namespace Phoenix.Api.Ardea.Pullers
{
    public abstract class WPPuller<TObviable> 
        where TObviable : class, IObviableModelEntity
    {
        public Dictionary<int, SchoolUnique> SchoolUqsDict { get; }
        public Dictionary<int, CourseUnique> CourseUqsDict { get; }

        protected List<int> PulledIds { get; set; }
        protected List<int> ObviatedIds { get; set; }
        protected ILogger Logger { get; }
        protected bool Verbose { get; }

        private readonly string _obviableTypeName = typeof(TObviable).Name;

        protected WPPuller(Dictionary<int, SchoolUnique> schoolUqsDict, Dictionary<int, CourseUnique> courseUqsDict,
            ILogger logger, bool verbose = true)
        {
            this.SchoolUqsDict = schoolUqsDict;
            this.CourseUqsDict = courseUqsDict;

            this.PulledIds = new();
            this.ObviatedIds = new();
            this.Logger = logger;
            this.Verbose = verbose;
        }

        protected WPPuller(Dictionary<int, SchoolUnique> schoolUqsDict, 
            ILogger logger, bool verbose = true)
        : this(schoolUqsDict, new(), logger, verbose) 
        {
        }

        protected WPPuller(ILogger logger, bool verbose = true)
            : this(new(), logger, verbose) 
        {
        }

        public abstract PostCategory PostCategory { get; }

        public abstract Task<List<int>> PullAsync();

        public abstract Task<List<int>> ObviateAsync(List<int> toKeep);

        public async Task<List<int>> ObviateAsync() => await ObviateAsync(PulledIds);

        public virtual async Task PutAsync()
        {
            _ = await PullAsync();
            _ = await ObviateAsync();
        }

        protected async Task<List<int>> ObviateAllPerSchoolAsync(Func<int, IEnumerable<TObviable>> findObviables,
            ObviableRepository<TObviable> repository, List<int> toKeepIds)
        {
            var obviableTypeNamePlural = _obviableTypeName + (_obviableTypeName.EndsWith('s') ? "" : "s");

            Logger.LogInformation("-----------------------------------------------------------------");
            Logger.LogInformation("{ObviableTypeName} obviation started.", obviableTypeNamePlural);

            var allObviatedIds = new List<int>();

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                Logger.LogInformation("Obviation of {ObviableTypeName} for school \"{SchoolUq}\".",
                    obviableTypeNamePlural.ToLower(), schoolUqPair.Value);

                var toObviate = findObviables(schoolUqPair.Key)
                    .Where(c => !toKeepIds.Contains(c.Id))
                    .ToList();
                
                allObviatedIds.AddRange(await ObviateGroupAsync(toObviate, repository));
            }

            Logger.LogInformation("{ObviableTypeName} obviation finished.", obviableTypeNamePlural);
            Logger.LogInformation("-----------------------------------------------------------------");

            return allObviatedIds;
        }

        protected async Task<List<int>> ObviateGroupAsync(IList<TObviable> toObviate, ObviableRepository<TObviable> repository)
        {
            if (!toObviate.Any())
            {
                if (Verbose)
                    Logger.LogInformation("There is no {ObviableTypeName} that needs to be obviated.",
                        _obviableTypeName.ToLower());
                return new();
            }

            if (Verbose)
                Logger.LogInformation("Obviating {ToObviateNum} {ObviableTypeName}s.",
                    toObviate.Count, _obviableTypeName.ToLower());

            var obviated = await repository.ObviateRangeAsync(toObviate);

            Logger.LogInformation("{ObviatedNum}/{ToObviateNum} {ObviableTypeName}s obviated successfully.",
                obviated.Count(), toObviate.Count, _obviableTypeName.ToLower());

            return obviated.Select(o => o.Id).ToList();
        }

        protected static async Task<Dictionary<int, string>> FindSchoolTimezonesAsync(PhoenixContext phoenixContext, IEnumerable<int> schoolIds)
        {
            var schoolRepository = new SchoolRepository(phoenixContext);
            var schoolTimezonesDict = new Dictionary<int, string>(schoolIds.Count());

            School? school;
            foreach (var schoolId in schoolIds)
            {
                school = await schoolRepository.FindPrimaryAsync(schoolId);
                if (school is null)
                    continue;

                schoolTimezonesDict.Add(schoolId, school.SchoolSetting.TimeZone);
            }

            return schoolTimezonesDict;
        }
    }
}
