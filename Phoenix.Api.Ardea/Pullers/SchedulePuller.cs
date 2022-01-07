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

        public List<int> UpdatedClassroomIds { get; private set; }

        public SchedulePuller(Dictionary<int, SchoolUnique> schoolUqsDict, Dictionary<int, CourseUnique> courseUqsDict, 
            PhoenixContext phoenixContext, ILogger logger, SchoolUnique? specificSchoolUq = null, bool verbose = true) 
            : base(schoolUqsDict, courseUqsDict, logger, specificSchoolUq, verbose)
        {
            this.scheduleRepository = new(phoenixContext);
            this.classroomRepository = new(phoenixContext);

            this.schoolTimezonesDict = FindSchoolTimezones(phoenixContext, schoolUqsDict.Keys);

            this.UpdatedClassroomIds = new List<int>();
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
                        if (UpdatedClassroomIds.Contains(classroom.Id))
                            continue;

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

                        UpdatedClassroomIds.Add(classroom.Id);
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

        public override int[] Delete(int[] toKeep)
        {
            Logger.LogInformation("------------------------");
            Logger.LogInformation("Schedules deletion started");

            var toDelete = scheduleRepository.Find().Where(s => !toKeep.Contains(s.Id));
            var deletedIds = new int[toDelete.Count()];

            int p = 0;
            foreach (var schedule in toDelete)
            {
                if (schedule.IsDeleted)
                {
                    if (Verbose)
                        Logger.LogInformation("Schedule with id {ScheduleId} already deleted", schedule.Id);
                    continue;
                }

                if (Verbose)
                    Logger.LogInformation("Deleting schedule with id {ScheduleId}", schedule.Id);

                schedule.IsDeleted = true;
                schedule.DeletedAt = DateTimeOffset.Now;
                scheduleRepository.Update(schedule);

                deletedIds[p++] = schedule.Id;
            }

            Logger.LogInformation("Schedules deletion finished");
            Logger.LogInformation("-------------------------");

            return deletedIds;
        }

        public int[] DeleteClassrooms()
        {
            Logger.LogInformation("---------------------------");
            Logger.LogInformation("Classrooms deletion started");

            var toDelete = classroomRepository.Find().Where(c => !UpdatedClassroomIds.Contains(c.Id));
            var deletedIds = new int[toDelete.Count()];

            int p = 0;
            foreach (var classroom in toDelete)
            {
                if (classroom.IsDeleted)
                {
                    if (Verbose)
                        Logger.LogInformation("Classroom with id {ClassroomId} already deleted", classroom.Id);
                    continue;
                }

                if (Verbose)
                    Logger.LogInformation("Deleting classroom with id {ClassroomId}", classroom.Id);

                classroom.IsDeleted = true;
                classroom.DeletedAt = DateTimeOffset.Now;
                classroomRepository.Update(classroom);

                deletedIds[p++] = classroom.Id;
            }

            Logger.LogInformation("Classrooms deletion finished");
            Logger.LogInformation("----------------------------");

            return deletedIds;
        }

        public override async Task PutAsync()
        {
            await base.PutAsync();

            _ = DeleteClassrooms();
        }
    }
}
