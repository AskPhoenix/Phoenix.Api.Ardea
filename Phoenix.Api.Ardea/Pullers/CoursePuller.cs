using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Types;
using Phoenix.DataHandle.DataEntry.Types.Uniques;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;

namespace Phoenix.Api.Ardea.Pullers
{
    public class CoursePuller : WPPuller<Course>
    {
        private readonly CourseRepository _courseRepository;
        private readonly BookRepository _bookRepository;
        public List<int> PulledBookIds { get; set; } = new();

        public override PostCategory PostCategory => PostCategory.Course;

        public CoursePuller(
            Dictionary<int, SchoolUnique> schoolUqsDict,
            PhoenixContext phoenixContext,
            ILogger logger,
            bool verbose = true)
            : base(schoolUqsDict, phoenixContext, logger, verbose)
        {
            _courseRepository = new(phoenixContext);
            _bookRepository = new(phoenixContext);

            _courseRepository.Include(c => c.Books);
        }

        public override async Task<List<int>> PullAsync()
        {
            _logger.LogInformation("-----------------------------------------------------------------");
            _logger.LogInformation("Courses & Books synchronization started.");

            var toCreate = new List<Course>();
            var toUpdate = new List<Course>();

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                try
                {
                    var posts = await this.GetPostsForSchoolAsync(schoolUqPair.Value);

                    _logger.LogInformation("{CoursesNumber} courses found for School \"{SchoolUq}\".",
                        posts.Count(), schoolUqPair.Value);

                    foreach (var coursePost in posts)
                    {
                        var courseAcf = await WPClientWrapper.GetCourseAcfAsync(coursePost);
                        if (courseAcf is null)
                        {
                            _logger.LogError("No ACF found for post {Title}", coursePost.GetTitle());
                            continue;
                        }

                        var courseUq = courseAcf.GetCourseUnique(schoolUqPair.Value);
                        var course = await _courseRepository.FindUniqueAsync(courseUq);

                        var booksToCreate = new List<Book>();
                        var booksExisting = new List<Book>();

                        var books = courseAcf.GetBooks(schoolUqPair.Key);

                        if (Verbose)
                        {
                            _logger.LogInformation("Synchronizing books for course \"{CourseUq}\".", courseUq);
                            _logger.LogInformation("{BooksNumber} books found.", books.Count);
                        }

                        foreach (var book in books)
                        {
                            var book1 = await _bookRepository.FindUniqueAsync(schoolUqPair.Key, book.Name);
                            if (book1 is null)
                            {
                                if (Verbose)
                                    _logger.LogInformation("Book \"{BookName}\" to be created.", book.Name);

                                booksToCreate.Add(book);
                            }
                            else
                            {
                                if (Verbose)
                                    _logger.LogInformation("Book \"{BookName}\" already exists.", book.Name);

                                booksExisting.Add(book1);
                            }
                        }

                        var booksCreated = new List<Book>();
                        if (booksToCreate.Any())
                        {
                            _logger.LogInformation("Creating {ToCreateNum} books...", booksToCreate.Count);
                            booksCreated = (await _bookRepository.CreateRangeAsync(booksToCreate)).ToList();
                            _logger.LogInformation("{CreatedNum}/{ToCreateNum} books created successfully.",
                                booksCreated.Count(), booksToCreate.Count);
                        }
                        else
                            _logger.LogInformation("No books to create.");

                        var booksFinal = booksCreated.Concat(booksExisting);
                        PulledBookIds.AddRange(booksFinal.Select(b => b.Id).ToList());

                        if (course is null)
                        {
                            if (Verbose)
                                _logger.LogInformation("Course {CourseUq} to be created.", courseUq);

                            course = courseAcf.ToCourse(schoolUqPair.Key, booksFinal);
                            toCreate.Add(course);
                        }
                        else
                        {
                            if (Verbose)
                                _logger.LogInformation("Course {CourseUq} to be updated.", courseUq);

                            courseAcf.ToCourse(course, schoolUqPair.Key, booksFinal);
                            toUpdate.Add(course);
                        }
                    }
                }
                catch(Exception ex)
                {
                    _logger.LogError(ex.Message);
                    _logger.LogWarning("Skipping post...");
                    continue;
                }
            }

            var created = new List<Course>();
            if (toCreate.Any())
            {
                _logger.LogInformation("Creating {ToCreateNum} courses...", toCreate.Count);
                created = (await _courseRepository.CreateRangeAsync(toCreate)).ToList();
                _logger.LogInformation("{CreatedNum}/{ToCreateNum} courses created successfully.",
                    created.Count(), toCreate.Count);
            }
            else
                _logger.LogInformation("No courses to create.");

            var updated = new List<Course>();
            if (toUpdate.Any())
            {
                _logger.LogInformation("Updating {ToUpdateNum} courses...", toUpdate.Count);
                updated = (await _courseRepository.UpdateRangeAsync(toUpdate)).ToList();
                await _courseRepository.RestoreRangeAsync(toUpdate);
                _logger.LogInformation("{UpdatedNum}/{ToUpdateNum} courses updated successfully.",
                    updated.Count(), toUpdate.Count);
            }
            else
                _logger.LogInformation("No courses to updated.");

            _logger.LogInformation("Courses & Books synchronization finished.");
            _logger.LogInformation("-----------------------------------------------------------------");

            foreach (var course in created.Concat(updated))
                CourseUqsDict.Add(course.Id, new CourseUnique(new SchoolUnique(course.School.Code), course.Code));

            PulledBookIds = PulledBookIds.Distinct().ToList();
            PulledIds.AddRange(CourseUqsDict.Keys);
            
            return PulledIds;
        }

        public override async Task<List<int>> ObviateAsync(List<int> toKeep)
        {
            // Books are never obviated

            IEnumerable<Course> findCoursesForSchool(int schoolId)
            {
                var school = Task.Run(() => _schoolRepository.FindPrimaryAsync(schoolId)).Result;
                if (school is null)
                    return Enumerable.Empty<Course>();

                return school.Courses;
            }

            return ObviatedIds = await ObviateAllPerSchoolAsync(
                findCoursesForSchool, _courseRepository, toKeep);
        }
    }
}
