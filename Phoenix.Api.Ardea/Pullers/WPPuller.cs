using Microsoft.EntityFrameworkCore;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Main.Models.Extensions;
using Phoenix.DataHandle.Repositories;
using Phoenix.DataHandle.WordPress.Models.Uniques;

namespace Phoenix.Api.Ardea.Pullers
{
    public abstract class WPPuller<TObviable> 
        where TObviable : class, IObviableModelEntity
    {
        // TODO: Use Async Repositories (UpdateAsync, ObviateAsync, etc.)
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

            this.PulledIds = new List<int>();
            this.ObviatedIds = new List<int>();
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

        public abstract int CategoryId { get; }

        public abstract Task<List<int>> PullAsync();

        public abstract Task<List<int>> ObviateAsync(List<int> toKeep);

        public async Task<List<int>> ObviateAsync() => await ObviateAsync(PulledIds);

        public virtual async Task PutAsync()
        {
            _ = await PullAsync();
            _ = await ObviateAsync();
        }

        protected async Task<List<int>> ObviateAllPerSchoolAsync(Func<int, IQueryable<TObviable>> findObviables,
            ObviableRepository<TObviable> repository, List<int> toKeepIds)
        {
            var obviableTypeNamePlural = _obviableTypeName + (_obviableTypeName.EndsWith('s') ? "" : "s");

            Logger.LogInformation("-----------------------------------------------------------------");
            Logger.LogInformation("{ObviableTypeName} obviation started", obviableTypeNamePlural);

            var allObviatedIds = new List<int>();

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                Logger.LogInformation("Obviation of {ObviableTypeName} for school \"{SchoolUq}\"",
                    obviableTypeNamePlural.ToLower(), schoolUqPair.Value);

                var toObviate = await findObviables(schoolUqPair.Key)
                    .Where(c => !toKeepIds.Contains(c.Id))
                    .ToListAsync();
                
                allObviatedIds.AddRange(ObviateGroup(toObviate, repository));
            }

            Logger.LogInformation("{ObviableTypeName} obviation finished", obviableTypeNamePlural);
            Logger.LogInformation("-----------------------------------------------------------------");

            return allObviatedIds;
        }

        protected List<int> ObviateGroup(IList<TObviable> toObviate, ObviableRepository<TObviable> repository)
        {
            if (!toObviate.Any())
            {
                if (Verbose)
                    Logger.LogInformation("There is no {ObviableTypeName} that needs to be obviated",
                        _obviableTypeName.ToLower());
                return new();
            }

            var obviatedIds = new List<int>(toObviate.Count());
            int? obviatedId;

            foreach (var obviable in toObviate)
            {
                obviatedId = ObviateUnit(obviable, repository);
                if (obviatedId != null)
                    obviatedIds.Add(obviatedId.Value);
            }

            return obviatedIds;
        }

        protected virtual int? ObviateUnit(TObviable obviable, ObviableRepository<TObviable> repository)
        {
            if (Verbose)
                Logger.LogInformation("Obviating {ObviableTypeName} with id {ObviableId}",
                    _obviableTypeName.ToLower(), obviable.Id);

            repository.Obviate(obviable);

            return obviable.Id;
        }

        protected static Dictionary<int, string> FindSchoolTimezones(PhoenixContext phoenixContext, IEnumerable<int> schoolIds)
        {
            var schoolRepository = new SchoolRepository(phoenixContext);
            var schoolTimezonesDict = new Dictionary<int, string>(schoolIds.Count());

            foreach (var schoolId in schoolIds)
                schoolTimezonesDict.Add(schoolId, schoolRepository.FindSchoolSettings(schoolId).TimeZone);

            return schoolTimezonesDict;
        }
    }
}
