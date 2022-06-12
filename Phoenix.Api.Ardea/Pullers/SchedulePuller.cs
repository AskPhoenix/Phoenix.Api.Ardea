using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Models.Uniques;
using Phoenix.DataHandle.Main.Entities;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using WordPressPCL.Models;

namespace Phoenix.Api.Ardea.Pullers
{
    public class SchedulePuller : WPPuller<Schedule>
    {
        private readonly ScheduleRepository scheduleRepository;
        private readonly ClassroomRepository classroomRepository;
        private readonly CourseRepository courseRepository;

        private readonly Dictionary<int, string> schoolTimezonesDict;

        public List<int> PulledClassroomIds { get; } = new();

        public override PostCategory PostCategory => PostCategory.Schedule;

        public SchedulePuller(Dictionary<int, SchoolUnique> schoolUqsDict, Dictionary<int, CourseUnique> courseUqsDict, 
            PhoenixContext phoenixContext, ILogger logger, bool verbose = true) 
            : base(schoolUqsDict, courseUqsDict, logger, verbose)
        {
            this.scheduleRepository = new(phoenixContext);
            this.classroomRepository = new(phoenixContext);
            this.courseRepository = new(phoenixContext);

            this.schoolTimezonesDict = Task.Run(() => FindSchoolTimezonesAsync(phoenixContext, schoolUqsDict.Keys)).Result;
        }

        public override async Task<List<int>> PullAsync()
        {
            Logger.LogInformation("-----------------------------------------------------------------");
            Logger.LogInformation("Schedules & Classrooms synchronization started.");

            IEnumerable<Post> schedulePosts = await WPClientWrapper.GetPostsAsync(this.PostCategory);
            IEnumerable<Post> filteredPosts;

            var toCreate = new List<Schedule>();
            var toUpdate = new List<Schedule>();
            var toUpdateFrom = new List<Schedule>();

            var classroomsToCreate = new List<Classroom>();
            var classroomsToUpdate = new List<Classroom>(); // Used only to store the classroom ids

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                filteredPosts = schedulePosts.FilterPostsForSchool(schoolUqPair.Value);

                Logger.LogInformation("{SchedulesNumber} Schedules found for School \"{SchoolUq}\".",
                    filteredPosts.Count(), schoolUqPair.Value);

                foreach (var schedulePost in filteredPosts)
                {
                    var scheduleAcf = await WPClientWrapper.GetScheduleAcfAsync(schedulePost);
                    var courseUq = new CourseUnique(schoolUqPair.Value, scheduleAcf.CourseCode);
                    var courseKv = CourseUqsDict.SingleOrDefault(kv => kv.Value.Equals(courseUq));

                    if (courseKv.Equals(default))
                    {
                        Logger.LogError("There is no course with code {CourseCode}.", courseUq.Code);
                        Logger.LogError("Schedule {SchedulePostTitle} is skipped.", schedulePost.GetTitle());

                        continue;
                    }

                    int courseId = courseKv.Key;

                    Classroom? classroom = null;

                    if (!string.IsNullOrEmpty(scheduleAcf.ClassroomName))
                    {
                        classroom = await classroomRepository.FindUniqueAsync(schoolUqPair.Key, scheduleAcf.ClassroomName);
                        
                        if (classroom is null)
                        {
                            if (Verbose)
                                Logger.LogInformation("Classroom \"{ClassroomName}\" to be created.", scheduleAcf.ClassroomName);

                            classroom = new()
                            {
                                SchoolId = schoolUqPair.Key,
                                Name = scheduleAcf.ClassroomName
                            };

                            classroomsToCreate.Add(classroom);
                        }
                        else
                        {
                            if (Verbose)
                                Logger.LogInformation("Classroom \"{ClassroomName}\" already exists.", classroom.Name);

                            classroomsToUpdate.Add(classroom);
                        }
                    }


                    var schedule = await this.scheduleRepository.FindUniqueAsync(courseId, scheduleAcf);
                    if (schedule is null)
                    {
                        if (Verbose)
                            Logger.LogInformation("Schedule for course with code {CourseCode} on {Day} at {Time} to be created.",
                                scheduleAcf.CourseCode, scheduleAcf.DayString, scheduleAcf.StartTime.ToString("HH:mm"));

                        schedule = (Schedule)(ISchedule)scheduleAcf;
                        schedule.ClassroomId = classroom?.Id;
                        schedule.CourseId = courseId;

                        toCreate.Add(schedule);
                    }
                    else
                    {
                        if (Verbose)
                            Logger.LogInformation("Updating schedule for course with code {CourseCode} on {Day} at {Time}.",
                                scheduleAcf.CourseCode, scheduleAcf.DayString, scheduleAcf.StartTime.ToString("HH:mm"));

                        var scheduleFrom = (Schedule)(ISchedule)scheduleAcf;
                        scheduleFrom.ClassroomId = classroom?.Id;

                        toUpdate.Add(schedule);
                        toUpdateFrom.Add(scheduleFrom);
                    }
                }
            }

            Logger.LogInformation("Creating {ToCreateNum} schedules...", toCreate.Count);
            var created = await scheduleRepository.CreateRangeAsync(toCreate);
            Logger.LogInformation("{CreatedNum}/{ToCreateNum} schedules created successfully.",
                created.Count(), toCreate.Count);

            Logger.LogInformation("Creating {ToCreateNum} classrooms...", classroomsToCreate.Count);
            var classroomsCreated = await classroomRepository.CreateRangeAsync(classroomsToCreate);
            Logger.LogInformation("{CreatedNum}/{ToCreateNum} classrooms created successfully.",
                classroomsCreated.Count(), classroomsToCreate.Count);

            Logger.LogInformation("Updating {ToUpdateNum} schedules...", toUpdate.Count);
            var updated = await scheduleRepository.UpdateRangeAsync(toUpdate, toUpdateFrom);
            var restored = await scheduleRepository.RestoreRangeAsync(toUpdate);
            Logger.LogInformation("{UpdatedNum}/{ToUpdateNum} schedules updated successfully.",
                updated.Count() + restored.Count(), toUpdate.Count);

            Logger.LogInformation("Schedules & Classrooms synchronization finished.");
            Logger.LogInformation("-----------------------------------------------------------------");

            PulledClassroomIds.AddRange(classroomsCreated.Concat(classroomsToUpdate).Select(c => c.Id).Distinct().ToList());
            PulledIds.AddRange(created.Concat(updated).Select(s => s.Id).Distinct().ToList());
            
            return PulledIds;
        }

        public override async Task<List<int>> ObviateAsync(List<int> toKeep)
        {
            // Classrooms are never obviated

            Logger.LogInformation("-----------------------------------------------------------------");
            Logger.LogInformation("Schedules obviation started.");

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                Logger.LogInformation("Obviation of schedules for courses of school \"{SchoolUq}\".", schoolUqPair.Value);

                var schoolCoursesUqsDict = CourseUqsDict.Where(kv => kv.Value.SchoolUnique.Equals(schoolUqPair.Value));
                foreach (var courseUqPair in schoolCoursesUqsDict)
                {
                    Logger.LogInformation("Obviation of schedules for course with code {CourseCode}.", courseUqPair.Value.Code);

                    var course = await courseRepository.FindPrimaryAsync(courseUqPair.Key);
                    if (course is null)
                        continue;

                    var toObviate = course.Schedules
                        .Where(s => !toKeep.Contains(s.Id))
                        .ToList();

                    ObviatedIds.AddRange(await ObviateGroupAsync(toObviate, scheduleRepository));
                }
            }

            Logger.LogInformation("Schedules obviation finished.");
            Logger.LogInformation("-----------------------------------------------------------------");

            return ObviatedIds;
        }
    }
}
