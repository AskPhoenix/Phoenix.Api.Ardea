using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using Phoenix.DataHandle.WordPress.Models.Uniques;

namespace Phoenix.Api.Ardea.Pullers
{
    public abstract class WPPuller
    {
        public Dictionary<int, SchoolUnique> SchoolUqsDict { get; }
        public Dictionary<int, CourseUnique> CourseUqsDict { get; }
        public SchoolUnique? SpecificSchoolUq { get; }
        public bool SpecificSchoolOnly { get; }

        protected ILogger Logger { get; }
        protected bool Verbose { get; }

        protected WPPuller(Dictionary<int, SchoolUnique> schoolUqsDict, Dictionary<int, CourseUnique> courseUqsDict,
            ILogger logger, SchoolUnique? specificSchoolUq = null, bool verbose = true)
        {
            this.SchoolUqsDict = schoolUqsDict;
            this.CourseUqsDict = courseUqsDict;
            
            this.Logger = logger;
            this.Verbose = verbose;

            this.SpecificSchoolUq = specificSchoolUq;
            this.SpecificSchoolOnly = specificSchoolUq != null;
        }

        protected WPPuller(Dictionary<int, SchoolUnique> schoolUqsDict, 
            ILogger logger, SchoolUnique? specificSchoolUq = null, bool verbose = true)
        : this(schoolUqsDict, new(0), logger, specificSchoolUq, verbose) 
        {
        }

        protected WPPuller(ILogger logger, SchoolUnique? specificSchoolUq = null, bool verbose = true)
            : this(new(0), logger, specificSchoolUq, verbose) 
        {
        }

        public abstract int CategoryId { get; }

        public abstract Task<int[]> PullAsync();
        public abstract int[] Delete(int[] toKeep); // TODO: make async (create async update method in repositories)
        public virtual async Task PutAsync()
        {
            var updatedIds = await PullAsync();

            if (SpecificSchoolOnly)
            {
                Logger.LogWarning("Data deletion skipped due to specific school mode.");
                return;
            }

            _ = Delete(updatedIds);
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
