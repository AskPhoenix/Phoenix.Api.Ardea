using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Models.Uniques;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;

namespace Phoenix.Api.Ardea.Pullers
{
    public class SchedulePuller : WPPuller<Schedule>
    {
        private readonly ScheduleRepository _scheduleRepository;
        private readonly ClassroomRepository _classroomRepository;
        private readonly CourseRepository _courseRepository;

        public List<int> PulledClassroomIds { get; set; } = new();

        public override PostCategory PostCategory => PostCategory.Schedule;

        public SchedulePuller(
            Dictionary<int, SchoolUnique> schoolUqsDict,
            Dictionary<int, CourseUnique> courseUqsDict, 
            PhoenixContext phoenixContext,
            ILogger logger,
            bool verbose = true) 
            : base(schoolUqsDict, courseUqsDict, phoenixContext, logger, verbose)
        {
            _scheduleRepository = new(phoenixContext);
            _classroomRepository = new(phoenixContext);
            _courseRepository = new(phoenixContext);
        }

        // TODO: Create/Update Lectures with Schedule?

        public override async Task<List<int>> PullAsync()
        {
            _logger.LogInformation("-----------------------------------------------------------------");
            _logger.LogInformation("Schedules & Classrooms synchronization started.");

            var toCreate = new List<Schedule>();
            var toUpdate = new List<Schedule>();

            var classroomsCreated = new List<Classroom>();
            var classroomsExisting = new List<Classroom>();

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                try
                {
                    var posts = await this.GetPostsForSchoolAsync(schoolUqPair.Value);

                    _logger.LogInformation("{SchedulesNumber} Schedules found for School \"{SchoolUq}\".",
                        posts.Count(), schoolUqPair.Value);

                    foreach (var schedulePost in posts)
                    {
                        var scheduleAcf = await WPClientWrapper.GetScheduleAcfAsync(schedulePost);
                        var courseUq = new CourseUnique(schoolUqPair.Value, scheduleAcf.CourseCode);
                        var courseIdUq = CourseUqsDict.SingleOrDefault(id_uq => id_uq.Value.Equals(courseUq));

                        if (courseIdUq.Equals(default))
                        {
                            _logger.LogError("There is no course with code {CourseCode}.", courseUq.Code);
                            _logger.LogError("Schedule {SchedulePostTitle} is skipped.", schedulePost.GetTitle());

                            continue;
                        }

                        bool hasClassroom = scheduleAcf.Classroom is not null;
                        Classroom? classroom = null;
                        if (hasClassroom)
                        {
                            classroom = await _classroomRepository.FindUniqueAsync(schoolUqPair.Key, scheduleAcf.ClassroomName!);

                            if (classroom is null)
                            {
                                if (Verbose)
                                    _logger.LogInformation("Creating Classroom \"{ClassroomName}\"...", scheduleAcf.ClassroomName);

                                classroom = scheduleAcf.Classroom;
                                classroom!.SchoolId = schoolUqPair.Key;

                                classroom = await _classroomRepository.CreateAsync(classroom);

                                classroomsCreated.Add(classroom);

                                _logger.LogInformation("Classroom \"{ClassroomName}\" created successfully.", scheduleAcf.ClassroomName);
                            }
                            else
                            {
                                if (Verbose)
                                    _logger.LogInformation("Classroom \"{ClassroomName}\" already exists.", classroom.Name);

                                classroomsExisting.Add(classroom);
                            }
                        }

                        var schedule = await this._scheduleRepository.FindUniqueAsync(courseIdUq.Key, scheduleAcf);
                        if (schedule is null)
                        {
                            if (Verbose)
                                _logger.LogInformation("Schedule for course \"{CourseUq}\" on {Day} at {Time} to be created.",
                                    courseUq, scheduleAcf.DayString, scheduleAcf.StartTime.ToString("HH:mm"));

                            schedule = scheduleAcf.ToSchedule(courseIdUq.Key, classroom?.Id);
                            toCreate.Add(schedule);
                        }
                        else
                        {
                            if (Verbose)
                                _logger.LogInformation("Schedule for course \"{CourseUq}\" on {Day} at {Time} to be updated.",
                                    courseUq, scheduleAcf.DayString, scheduleAcf.StartTime.ToString("HH:mm"));

                            scheduleAcf.ToSchedule(schedule, courseIdUq.Key, classroom?.Id);
                            toUpdate.Add(schedule);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                    _logger.LogWarning("Skipping post...");
                    continue;
                }
            }

            var created = new List<Schedule>();
            if (toCreate.Any())
            {
                _logger.LogInformation("Creating {ToCreateNum} schedules...", toCreate.Count);
                created = (await _scheduleRepository.CreateRangeAsync(toCreate)).ToList();
                _logger.LogInformation("{CreatedNum}/{ToCreateNum} schedules created successfully.",
                    created.Count(), toCreate.Count);
            }
            else
                _logger.LogInformation("No schedules to create.");

            var updated = new List<Schedule>();
            if (toUpdate.Any())
            {
                _logger.LogInformation("Updating {ToUpdateNum} schedules...", toUpdate.Count);
                updated = (await _scheduleRepository.UpdateRangeAsync(toUpdate)).ToList();
                await _scheduleRepository.RestoreRangeAsync(toUpdate);
                _logger.LogInformation("{UpdatedNum}/{ToUpdateNum} schedules updated successfully.",
                    updated.Count(), toUpdate.Count);
            }
            else
                _logger.LogInformation("No schedules to update.");

            _logger.LogInformation("Schedules & Classrooms synchronization finished.");
            _logger.LogInformation("-----------------------------------------------------------------");

            PulledClassroomIds.AddRange(classroomsCreated.Concat(classroomsExisting).Select(c => c.Id).Distinct().ToList());
            PulledIds.AddRange(created.Concat(updated).Select(s => s.Id).Distinct().ToList());
            
            return PulledIds;
        }

        public override async Task<List<int>> ObviateAsync(List<int> toKeep)
        {
            // TODO: Obviate Classrooms if they aren't assigned to any schedules

            _logger.LogInformation("-----------------------------------------------------------------");
            _logger.LogInformation("Schedules obviation started.");

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                _logger.LogInformation("Obviation of schedules for courses of school \"{SchoolUq}\".", schoolUqPair.Value);

                var schoolCoursesUqsDict = CourseUqsDict.Where(kv => kv.Value.SchoolUnique.Equals(schoolUqPair.Value));
                foreach (var courseUqPair in schoolCoursesUqsDict)
                {
                    _logger.LogInformation("Obviation of schedules for course \"{CourseUq}\".", courseUqPair.Value);

                    var course = await _courseRepository.FindPrimaryAsync(courseUqPair.Key);
                    if (course is null)
                        continue;

                    var toObviate = course.Schedules
                        .Where(s => !toKeep.Contains(s.Id))
                        .ToList();

                    ObviatedIds.AddRange(await ObviateGroupAsync(toObviate, _scheduleRepository));
                }
            }

            _logger.LogInformation("Schedules obviation finished.");
            _logger.LogInformation("-----------------------------------------------------------------");

            return ObviatedIds;
        }
    }
}
