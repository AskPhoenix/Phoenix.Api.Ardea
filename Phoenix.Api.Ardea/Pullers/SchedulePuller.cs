using Microsoft.EntityFrameworkCore;
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
    public class SchedulePuller : WPPuller<Schedule>
    {
        private readonly ScheduleRepository scheduleRepository;
        private readonly ClassroomRepository classroomRepository;
        private readonly CourseRepository courseRepository;

        private readonly Dictionary<int, string> schoolTimezonesDict;

        public List<int> PulledClassroomIds { get; private set; }

        public SchedulePuller(Dictionary<int, SchoolUnique> schoolUqsDict, Dictionary<int, CourseUnique> courseUqsDict, 
            PhoenixContext phoenixContext, ILogger logger, bool verbose = true) 
            : base(schoolUqsDict, courseUqsDict, logger, verbose)
        {
            this.scheduleRepository = new(phoenixContext);
            this.classroomRepository = new(phoenixContext);
            this.courseRepository = new(phoenixContext);

            this.schoolTimezonesDict = FindSchoolTimezones(phoenixContext, schoolUqsDict.Keys);

            this.PulledClassroomIds = new List<int>();
        }

        public override int CategoryId => PostCategoryWrapper.GetCategoryId(PostCategory.Schedule);

        public override async Task<List<int>> PullAsync()
        {
            Logger.LogInformation("-----------------------------------------------------------------");
            Logger.LogInformation("Schedules & Classrooms synchronization started");

            IEnumerable<Post> schedulePosts = await WordPressClientWrapper.GetPostsAsync(CategoryId);
            IEnumerable<Post> filteredPosts;

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                filteredPosts = schedulePosts.FilterPostsForSchool(schoolUqPair.Value);

                Logger.LogInformation("{SchedulesNumber} Schedules found for School \"{SchoolUq}\"",
                    filteredPosts.Count(), schoolUqPair.Value);

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
                                Logger.LogInformation("Adding classroom: {ClassroomName}", scheduleAcf.ClassroomName);
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
                                Logger.LogInformation("Classroom \"{ClassroomName}\" already exists", classroom.Name);
                        }
                        
                        PulledClassroomIds.Add(classroom.Id);
                    }

                    var schedule = await this.scheduleRepository.Find(scheduleAcf.MatchesUnique);
                    if (schedule is null)
                    {
                        if (Verbose)
                            Logger.LogInformation("Adding schedule for course with code {CourseCode} on {Day} at {Time}",
                                scheduleAcf.CourseCode, scheduleAcf.InvariantDayOfWeek, scheduleAcf.StartTime.ToString("HH:mm"));

                        schedule = scheduleAcf.ToContext();
                        schedule.ClassroomId = classroom?.Id;
                        try
                        {
                            schedule.CourseId = CourseUqsDict.Single(kv => kv.Value.Equals(courseUq)).Key;
                        }
                        catch (InvalidOperationException)
                        {
                            Logger.LogError("There is no course with code {CourseCode}", courseUq.Code);
                            Logger.LogError("Schedule {SchedulePostTitle} is skipped", schedulePost.GetTitle());

                            continue;
                        }
                        
                        scheduleRepository.Create(schedule);
                    }
                    else
                    {
                        if (Verbose)
                            Logger.LogInformation("Updating schedule for course with code {CourseCode} on {Day} at {Time}",
                                scheduleAcf.CourseCode, scheduleAcf.InvariantDayOfWeek, scheduleAcf.StartTime.ToString("HH:mm"));

                        var scheduleFrom = scheduleAcf.ToContext();
                        scheduleFrom.ClassroomId = classroom?.Id;

                        scheduleRepository.Update(schedule, scheduleFrom);
                        scheduleRepository.Restore(schedule);
                    }

                    PulledIds.Add(schedule.Id);
                }
            }

            Logger.LogInformation("Schedules & Classrooms synchronization finished");
            Logger.LogInformation("-----------------------------------------------------------------");

            PulledClassroomIds = PulledClassroomIds.Distinct().ToList();
            return PulledIds = PulledIds.Distinct().ToList();
        }

        public override async Task<List<int>> ObviateAsync(List<int> toKeep)
        {
            // Classrooms are never obviated

            Logger.LogInformation("-----------------------------------------------------------------");
            Logger.LogInformation("Schedules obviation started");

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                Logger.LogInformation("Obviation of schedules for courses of school \"{SchoolUq}\"", schoolUqPair.Value);

                var schoolCoursesUqsDict = CourseUqsDict.Where(kv => kv.Value.SchoolUnique.Equals(schoolUqPair.Value));
                foreach (var courseUqPair in schoolCoursesUqsDict)
                {
                    Logger.LogInformation("Obviation of schedules for course with code {CourseCode}", courseUqPair.Value.Code);

                    var toObviate = await courseRepository.FindSchedules(courseUqPair.Key)
                        .Where(s => !toKeep.Contains(s.Id))
                        .ToListAsync();

                    ObviatedIds.AddRange(ObviateGroup(toObviate, scheduleRepository));
                }
            }

            Logger.LogInformation("Schedules obviation finished");
            Logger.LogInformation("-----------------------------------------------------------------");

            return ObviatedIds;
        }
    }
}
