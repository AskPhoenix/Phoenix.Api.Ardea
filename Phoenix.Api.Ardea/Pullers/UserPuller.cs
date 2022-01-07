using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using Phoenix.DataHandle.WordPress.Models.Uniques;

namespace Phoenix.Api.Ardea.Pullers
{
    public abstract class UserPuller : WPPuller
    {
        protected readonly AspNetUserRepository aspNetUserRepository;

        protected UserPuller(Dictionary<int, SchoolUnique> schoolUqsDict, Dictionary<int, CourseUnique> courseUqsDict,
            PhoenixContext phoenixContext, ILogger logger, SchoolUnique? specificSchoolUq = null, bool verbose = true)
            : base(schoolUqsDict, courseUqsDict, logger, specificSchoolUq, verbose)
        {
            this.aspNetUserRepository = new(phoenixContext);
            this.aspNetUserRepository.Include(u => u.User);
            this.aspNetUserRepository.Include(u => u.ParenthoodChild);
            this.aspNetUserRepository.Include(u => u.ParenthoodParent);
        }

        public override int[] Delete(int[] toKeep)
        {
            Logger.LogInformation("------------------------");
            Logger.LogInformation("Users deletion started");

            var toDelete = aspNetUserRepository.Find().Where(u => !toKeep.Contains(u.Id));
            var deletedIds = new int[toDelete.Count()];

            int p = 0;
            foreach (var aspNetUser in toDelete)
            {
                if (aspNetUser.IsDeleted)
                {
                    if (Verbose)
                        Logger.LogInformation("User with id {UserId} already deleted", aspNetUser.Id);
                    continue;
                }

                if (Verbose)
                    Logger.LogInformation("Deleting user with id {UserId}", aspNetUser.Id);

                aspNetUser.IsDeleted = true;
                aspNetUser.DeletedAt = DateTimeOffset.Now;
                aspNetUserRepository.Update(aspNetUser);

                deletedIds[p++] = aspNetUser.Id;
            }

            Logger.LogInformation("Users deletion finished");
            Logger.LogInformation("-------------------------");

            return deletedIds;
        }
    }
}
