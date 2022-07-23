using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Types;
using Phoenix.DataHandle.DataEntry.Types.Uniques;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Main.Models.Extensions;
using Phoenix.DataHandle.Repositories;
using WordPressPCL.Models;

namespace Phoenix.Api.Ardea.Pullers
{
    public abstract class WPPuller<TObviable> 
        where TObviable : class, IObviableModelEntity
    {
        protected readonly PhoenixContext _phoenixContext;
        protected readonly SchoolRepository _schoolRepository;
        protected readonly ILogger _logger;

        public Dictionary<int, SchoolUnique> SchoolUqsDict { get; }
        public Dictionary<int, CourseUnique> CourseUqsDict { get; }
        
        protected List<int> PulledIds { get; set; }
        protected List<int> ObviatedIds { get; set; }
        protected bool Verbose { get; }

        private readonly string obviableTypeName = typeof(TObviable).Name;

        protected WPPuller(
            Dictionary<int, SchoolUnique> schoolUqsDict,
            Dictionary<int, CourseUnique> courseUqsDict,
            PhoenixContext phoenixContext,
            ILogger logger,
            bool verbose = true)
        {
            this._phoenixContext = phoenixContext;
            this._schoolRepository = new(phoenixContext);
            this._logger = logger;

            this.SchoolUqsDict = schoolUqsDict;
            this.CourseUqsDict = courseUqsDict;

            this.PulledIds = new();
            this.ObviatedIds = new();
            
            this.Verbose = verbose;
        }

        protected WPPuller(
            Dictionary<int, SchoolUnique> schoolUqsDict,
            PhoenixContext phoenixContext,
            ILogger logger,
            bool verbose = true)
            : this(schoolUqsDict, new(), phoenixContext, logger, verbose)
        {
        }

        protected WPPuller(
            PhoenixContext phoenixContext,
            ILogger logger,
            bool verbose = true)
            : this(new Dictionary<int, SchoolUnique>(), phoenixContext, logger, verbose)
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

        // TODO: Make sure that search returns all the requested posts
        protected async Task<IEnumerable<Post>> GetPostsForSchoolAsync(SchoolUnique schoolUq)
        {
            return (await WPClientWrapper
                    .GetPostsAsync(this.PostCategory, schoolUq.ToString()))
                    .FilterPostsForSchool(schoolUq);
        }

        protected async Task<List<int>> ObviateAllPerSchoolAsync(Func<int, IEnumerable<TObviable>> findObviables,
            ObviableRepository<TObviable> repository, List<int> toKeepIds)
        {
            var obviableTypeNamePlural = obviableTypeName + (obviableTypeName.EndsWith('s') ? "" : "s");

            _logger.LogInformation("-----------------------------------------------------------------");
            _logger.LogInformation("{ObviableTypeName} obviation started.", obviableTypeNamePlural);

            var allObviatedIds = new List<int>();

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                _logger.LogInformation("Obviation of {ObviableTypeName} for school \"{SchoolUq}\".",
                    obviableTypeNamePlural.ToLower(), schoolUqPair.Value);

                var toObviate = findObviables(schoolUqPair.Key)
                    .Where(o => !toKeepIds.Contains(o.Id))
                    .Where(o => !o.IsObviated)
                    .ToList();
                
                allObviatedIds.AddRange(await ObviateGroupAsync(toObviate, repository));
            }

            _logger.LogInformation("{ObviableTypeName} obviation finished.", obviableTypeNamePlural);
            _logger.LogInformation("-----------------------------------------------------------------");

            return allObviatedIds;
        }

        protected virtual async Task<List<int>> ObviateGroupAsync(IList<TObviable> toObviate, ObviableRepository<TObviable> repository)
        {
            if (!toObviate.Any())
            {
                if (Verbose)
                    _logger.LogInformation("There is no {ObviableTypeName} that needs to be obviated.",
                        obviableTypeName.ToLower());
                return new();
            }

            if (Verbose)
                _logger.LogInformation("Obviating {ToObviateNum} {ObviableTypeName}s...",
                    toObviate.Count, obviableTypeName.ToLower());

            var obviated = await repository.ObviateRangeAsync(toObviate);

            _logger.LogInformation("{ObviatedNum}/{ToObviateNum} {ObviableTypeName}s obviated successfully.",
                obviated.Count(), toObviate.Count, obviableTypeName.ToLower());

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
