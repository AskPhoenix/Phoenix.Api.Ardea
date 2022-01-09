using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using Phoenix.DataHandle.WordPress.Models.Uniques;

namespace Phoenix.Api.Ardea.Pullers
{
    public abstract class UserPuller : WPPuller
    {
        protected readonly AspNetUserRepository aspNetUserRepository;
        protected readonly SchoolRepository schoolRepository;

        protected UserPuller(Dictionary<int, SchoolUnique> schoolUqsDict, Dictionary<int, CourseUnique> courseUqsDict,
            PhoenixContext phoenixContext, ILogger logger, bool verbose = true)
            : base(schoolUqsDict, courseUqsDict, logger, verbose)
        {
            this.aspNetUserRepository = new(phoenixContext);
            this.aspNetUserRepository.Include(u => u.User);
            this.aspNetUserRepository.Include(u => u.ParenthoodChild);
            this.aspNetUserRepository.Include(u => u.ParenthoodParent);
            this.schoolRepository = new(phoenixContext);
        }

        public override List<int> Obviate()
        {
            return ObviatedIds = ObviateForSchools<AspNetUsers>(schoolRepository.FindUsers, aspNetUserRepository);
        }
    }
}
