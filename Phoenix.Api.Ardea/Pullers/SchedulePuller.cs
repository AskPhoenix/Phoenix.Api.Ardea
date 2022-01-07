using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using Phoenix.DataHandle.WordPress;
using Phoenix.DataHandle.WordPress.Models;
using Phoenix.DataHandle.WordPress.Models.Uniques;
using Phoenix.DataHandle.WordPress.Utilities;
using Phoenix.DataHandle.WordPress.Wrappers;
using WordPressPCL.Models;

namespace Phoenix.Api.Ardea.Pullers
{
    public class SchedulePuller : WPPuller
    {
        private readonly ScheduleRepository scheduleRepository;
        private readonly ClassroomRepository classroomRepository;

        private readonly Dictionary<int, string> schoolTimezonesDict;

        public SchedulePuller(Dictionary<int, SchoolUnique> schoolUqsDict, Dictionary<int, CourseUnique> courseUqsDict, 
            PhoenixContext phoenixContext, ILogger logger, SchoolUnique? specificSchoolUq = null, bool verbose = true) 
            : base(schoolUqsDict, courseUqsDict, logger, specificSchoolUq, verbose)
        {
            this.scheduleRepository = new(phoenixContext);
            this.classroomRepository = new(phoenixContext);

            this.schoolTimezonesDict = FindSchoolTimezones(phoenixContext, schoolUqsDict.Keys);
        }

        public override int CategoryId => PostCategoryWrapper.GetCategoryId(PostCategory.Schedule);

        public override async Task<int[]> PullAsync()
        {
            Logger.LogInformation("----------------------------------------------");
            Logger.LogInformation("Schedules & Classrooms synchronization started");

            IEnumerable<Post> schedulePosts = await WordPressClientWrapper.GetPostsAsync(CategoryId);
            IEnumerable<Post> filteredPosts;

            int P = schedulePosts.Count();
            int[] updatedIds = new int[P];

            int p = 0;
            foreach (var schoolUqPair in SchoolUqsDict)
            {
                filteredPosts = schedulePosts.FilterPostsForSchool(schoolUqPair.Value);

                Logger.LogInformation("{SchedulesNumber} Courses found for School \"{SchoolUq}\"",
                    filteredPosts.Count(), schoolUqPair.Value.ToString());

                foreach (var schedulePost in filteredPosts)
                {
                    var scheduleAcf = (ScheduleACF)(await WordPressClientWrapper.GetAcfAsync<ScheduleACF>(schedulePost.Id)).WithTitleCase();
                    scheduleAcf.SchoolUnique = schoolUqPair.Value;
                    scheduleAcf.SchoolTimeZone = schoolTimezonesDict[schoolUqPair.Key];

                    var courseUq = new CourseUnique(scheduleAcf.SchoolUnique, scheduleAcf.CourseCode);

                    Classroom? classroom = null;

                    if (!string.IsNullOrEmpty(scheduleAcf.ClassroomName))
                    {
                        classroom = classroomRepository.Find(schoolUqPair.Key, scheduleAcf.ClassroomName);

                        if (classroom is null)
                        {
                            if (Verbose)
                                Logger.LogInformation("Adding Classroom {ClassroomName} in School \"{SchoolUq}\"",
                                    scheduleAcf.ClassroomName, schoolUqPair.Value.ToString());
                            classroom = new Classroom()
                            {
                                SchoolId = schoolUqPair.Key,
                                Name = scheduleAcf.ClassroomName
                            };

                            this.classroomRepository.Create(classroom);
                        }
                        else
                        {
                            if (Verbose)
                                Logger.LogInformation("Classroom {ClassroomName} with id {ClassroomId} already exists in School \"{SchoolUq}\"",
                                    classroom.Name, classroom.Id, schoolUqPair.ToString());
                        }
                    }

                    var schedule = await this.scheduleRepository.Find(scheduleAcf.MatchesUnique);
                    if (schedule is null)
                    {
                        if (Verbose)
                            Logger.LogInformation("Adding Schedule: {SchedulePostTitle}", schedulePost.GetTitle());

                        schedule = scheduleAcf.ToContext();
                        schedule.CourseId = CourseUqsDict.Single(kv => kv.Value == courseUq).Key;
                        schedule.ClassroomId = classroom?.Id;

                        scheduleRepository.Create(schedule);
                    }
                    else
                    {
                        if (Verbose)
                            Logger.LogInformation("Updating Schedule: {SchedulePostTitle}", schedulePost.GetTitle());

                        var scheduleFrom = scheduleAcf.ToContext();
                        scheduleFrom.ClassroomId = classroom?.Id;

                        scheduleRepository.Update(schedule, scheduleFrom);
                    }

                    updatedIds[p++] = schedule.Id;
                }
            }

            Logger.LogInformation("Schedules & Classrooms synchronization finished");
            Logger.LogInformation("-----------------------------------------------");

            return updatedIds;
        }

        public override Task<int[]> DeleteAsync(int[] toKeep)
        {
            throw new NotImplementedException();
        }
    }
}
