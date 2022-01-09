using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Main.Models.Extensions;
using Phoenix.DataHandle.Repositories;
using Phoenix.DataHandle.WordPress.Models.Uniques;

namespace Phoenix.Api.Ardea.Pullers
{
    public abstract class WPPuller
    {
        public Dictionary<int, SchoolUnique> SchoolUqsDict { get; }
        public Dictionary<int, CourseUnique> CourseUqsDict { get; }
        
        protected List<int> PulledIds { get; set; }
        protected List<int> ObviatedIds { get; set; }
        protected ILogger Logger { get; }
        protected bool Verbose { get; }

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
        public abstract List<int> Obviate(); // TODO: make async (create async update method in repositories)
        public virtual async Task PutAsync()
        {
            _ = await PullAsync();
            _ = Obviate();
        }

        protected List<int> ObviateForSchools<TObviable>(
            Func<int, IQueryable<TObviable>> findObviables,
            ObviableRepository<TObviable> repository,
            List<int>? pulledIds = null)
            where TObviable : class, IObviableModelEntity
        {
            var obviableTypeName = typeof(TObviable).Name;
            var obviableTypeNamePlural = obviableTypeName + (obviableTypeName.EndsWith('s') ? "" : "s");

            pulledIds ??= PulledIds;

            Logger.LogInformation("-----------------------------");
            Logger.LogInformation("{ObviableTypeNamePlural} obviation started", obviableTypeNamePlural);

            var allObviatedIds = new List<int>();

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                Logger.LogInformation("Obviation of {ObviableTypeNamePlural} for school \"{SchoolUq}\"",
                    obviableTypeNamePlural.ToLower(), schoolUqPair.Value);

                var toObviate = findObviables(schoolUqPair.Key).Where(c => !pulledIds.Contains(c.Id));

                allObviatedIds.AddRange(ObviateGroup<TObviable>(toObviate, repository));
            }

            Logger.LogInformation("{ObviableTypeNamePlural} obviation finished", obviableTypeNamePlural);
            Logger.LogInformation("-----------------------------");

            return allObviatedIds;
        }

        protected List<int> ObviateGroup<TObviable>(
            IEnumerable<TObviable> toObviate, 
            ObviableRepository<TObviable> repository) 
            where TObviable : class, IObviableModelEntity
        {
            //var obviableTypeName = toObviate.GetType().GenericTypeArguments[0].Name;
            var obviableTypeName = typeof(TObviable).Name;

            if (!toObviate.Any())
            {
                if (Verbose)
                    Logger.LogInformation("There is no {ObviableTypeName} that needs to be obviated",
                        obviableTypeName.ToLower());
                return new();
            }

            var obviatedIds = new List<int>(toObviate.Count());

            foreach (var obviable in toObviate)
            {
                if (obviable.IsObviated)
                {
                    if (Verbose)
                        Logger.LogInformation("{ObviableTypeName} with id {ObviableId} is already obviated", 
                            obviableTypeName, obviable.Id);
                    continue;
                }

                if (Verbose)
                    Logger.LogInformation("Obviating {ObviableTypeName} with id {ObviableId}",
                        obviableTypeName.ToLower(), obviable.Id);

                repository.Obviate(obviable);
                obviatedIds.Add(obviable.Id);
            }

            return obviatedIds;
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
