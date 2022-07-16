using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Models.Uniques;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Main.Types;
using Phoenix.DataHandle.Repositories;

namespace Phoenix.Api.Ardea.Pullers
{
    public class SchedulePuller : WPPuller<Schedule>
    {
        private readonly ScheduleRepository _scheduleRepository;
        private readonly ClassroomRepository _classroomRepository;
        private readonly CourseRepository _courseRepository;
        private readonly LectureRepository _lectureRepository;

        public List<int> PulledClassroomIds { get; set; } = new();
        public List<int> PulledLectureIds { get; set; } = new();
        public List<int> ObviatedClassroomIds { get; set; } = new();
        public List<int> ObviatedLectureIds { get; set; } = new();

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
            _lectureRepository = new(phoenixContext);
        }

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
                                await _classroomRepository.RestoreAsync(classroom);
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

            var pulledSchedules = created.Concat(updated);
            foreach (var schedule in pulledSchedules)
                PulledLectureIds.AddRange(await this.PutLecturesForScheduleAsync(schedule));

            _logger.LogInformation("Schedules & Classrooms synchronization finished.");
            _logger.LogInformation("-----------------------------------------------------------------");

            PulledIds.AddRange(pulledSchedules.Select(s => s.Id).Distinct().ToList());
            PulledClassroomIds.AddRange(classroomsCreated.Concat(classroomsExisting).Select(c => c.Id));

            PulledClassroomIds = PulledClassroomIds.Distinct().ToList();
            PulledLectureIds = PulledLectureIds.Distinct().ToList();

            return PulledIds;
        }

        public override async Task<List<int>> ObviateAsync(List<int> toKeep)
        {
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

        public async Task<List<int>> ObviateClassroomsAsync(List<int> classroomsToKeep)
        {
            _logger.LogInformation("-----------------------------------------------------------------");
            _logger.LogInformation("Classrooms obviation started.");

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                _logger.LogInformation("Obviating classrooms for school \"{SchoolUq}\"...", schoolUqPair.Value);

                var school = await _schoolRepository.FindPrimaryAsync(schoolUqPair.Key);
                
                var classroomsToObviate = school!.Classrooms
                    .Where(c => !classroomsToKeep.Contains(c.Id))
                    .ToList();

                if (!classroomsToObviate.Any())
                {
                    if (Verbose)
                        _logger.LogInformation("There is no classroom that needs to be obviated.");

                    continue;
                }
                
                if (Verbose)
                    _logger.LogInformation("Obviating {ToObviateNum} classrooms...", classroomsToObviate.Count);

                var classroomsObviated = await _classroomRepository.ObviateRangeAsync(classroomsToObviate);

                _logger.LogInformation("{ObviatedNum}/{ToObviateNum} classrooms obviated successfully.",
                    classroomsObviated.Count(), classroomsToObviate.Count);

                ObviatedClassroomIds.AddRange(classroomsObviated.Select(o => o.Id));
            }

            _logger.LogInformation("Classrooms obviation finished.");
            _logger.LogInformation("-----------------------------------------------------------------");

            return ObviatedClassroomIds;
        }

        public Task<List<int>> ObviateClassroomsAsync() =>
            ObviateClassroomsAsync(this.PulledClassroomIds);

        private async Task<List<int>> PutLecturesForScheduleAsync(Schedule schedule)
        {
            if (schedule is null)
                throw new ArgumentNullException(nameof(schedule));

            var course = schedule.Course;
            var school = course.School;
            var zone = TimeZoneInfo.FindSystemTimeZoneById(school.SchoolSetting.TimeZone);

            _logger.LogInformation("Generating lectures for course \"{CourseUq}\" on {Day} at {Time}...",
                new CourseUnique(new(school.Code), course.Code), schedule.DayOfWeek,
                schedule.StartTime.ToString("HH:mm"));

            var days = Enumerable
                .Range(0, 1 + course.LastDate.Date.Subtract(course.FirstDate.Date).Days)
                .Select(i => course.FirstDate.Date.AddDays(i))
                .Where(d => d.DayOfWeek == schedule.DayOfWeek);

            var lecturesToCreate = new List<Lecture>(days.Count());
            var lecturesToUpdate = new List<Lecture>(days.Count());
            
            foreach (var day in days)
            {
                var s = day.Add(schedule.StartTime.TimeOfDay);
                var e = day.Add(schedule.EndTime.TimeOfDay);

                var start = new DateTimeOffset(s, zone.GetUtcOffset(s));
                var end = new DateTimeOffset(e, zone.GetUtcOffset(e));

                var lecture = await _lectureRepository.FindUniqueAsync(course.Id, start);
                
                if (lecture is null)
                {
                    lecture = new()
                    {
                        CourseId = course.Id,
                        ClassroomId = schedule.ClassroomId,
                        ScheduleId = schedule.Id,
                        StartDateTime = start,
                        EndDateTime = end,
                        Occasion = LectureOccasion.Scheduled
                    };

                    lecturesToCreate.Add(lecture);
                }
                else
                {
                    if (lecture.Occasion == LectureOccasion.Scheduled)
                    {
                        lecture.ClassroomId = schedule.ClassroomId;
                        lecture.EndDateTime = end;

                        lecturesToUpdate.Add(lecture);
                    }
                }
            }

            var lecturesCreated = new List<Lecture>();
            if (lecturesToCreate.Any())
            {
                _logger.LogInformation("Creating {ToCreateNum} lectures...", lecturesToCreate.Count);
                lecturesCreated = (await _lectureRepository.CreateRangeAsync(lecturesToCreate)).ToList();
                _logger.LogInformation("{CreatedNum}/{ToCreateNum} lectures created successfully.",
                    lecturesCreated.Count(), lecturesToCreate.Count);
            }
            else
                _logger.LogInformation("No lectures to create.");

            var lecturesUpdated = new List<Lecture>();
            if (lecturesToUpdate.Any())
            {
                _logger.LogInformation("Updating {ToUpdateNum} lectures...", lecturesToUpdate.Count);
                lecturesUpdated = (await _lectureRepository.UpdateRangeAsync(lecturesToUpdate)).ToList();
                await _lectureRepository.RestoreRangeAsync(lecturesToUpdate);
                _logger.LogInformation("{UpdatedNum}/{ToUpdateNum} lectures updated successfully.",
                    lecturesUpdated.Count(), lecturesToUpdate.Count);
            }
            else
                _logger.LogInformation("No lectures to update.");

            return lecturesCreated.Concat(lecturesUpdated).Select(l => l.Id).Distinct().ToList();
        }

        public async Task<List<int>> ObviateLecturesAsync(List<int> lecturesToKeep)
        {
            _logger.LogInformation("-----------------------------------------------------------------");
            _logger.LogInformation("Lectures obviation started.");

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                _logger.LogInformation("Obviating lectures for school \"{SchoolUq}\"...", schoolUqPair.Value);

                var school = await _schoolRepository.FindPrimaryAsync(schoolUqPair.Key);

                var lecturesToObviate = school!.Courses
                    .SelectMany(c => c.Lectures)
                    .Where(l => !lecturesToKeep.Contains(l.Id))
                    .Where(l => l.Occasion == LectureOccasion.Scheduled)
                    .ToList();

                if (!lecturesToObviate.Any())
                {
                    if (Verbose)
                        _logger.LogInformation("There is no lecture that needs to be obviated.");

                    continue;
                }

                if (Verbose)
                    _logger.LogInformation("Obviating {ToObviateNum} lectures...", lecturesToObviate.Count);

                var lecturesObviated = await _lectureRepository.ObviateRangeAsync(lecturesToObviate);

                _logger.LogInformation("{ObviatedNum}/{ToObviateNum} lectures obviated successfully.",
                    lecturesObviated.Count(), lecturesToObviate.Count);

                ObviatedLectureIds.AddRange(lecturesObviated.Select(o => o.Id));
            }

            _logger.LogInformation("Lectures obviation finished.");
            _logger.LogInformation("-----------------------------------------------------------------");

            return ObviatedClassroomIds;
        }

        public Task<List<int>> ObviateLecturesAsync() =>
            ObviateLecturesAsync(this.PulledLectureIds);

        public override async Task PutAsync()
        {
            await base.PutAsync();
            await ObviateClassroomsAsync();
            await ObviateLecturesAsync();
        }
    }
}
