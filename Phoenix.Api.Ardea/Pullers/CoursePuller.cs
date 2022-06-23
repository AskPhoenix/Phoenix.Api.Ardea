using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Models.Uniques;
using Phoenix.DataHandle.Main.Entities;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using WordPressPCL.Models;

namespace Phoenix.Api.Ardea.Pullers
{
    public class CoursePuller : WPPuller<Course>
    {
        private readonly CourseRepository _courseRepository;
        private readonly BookRepository _bookRepository;
        private readonly SchoolRepository _schoolRepository;

        // TODO: To Remove?
        private readonly Dictionary<int, string> schoolTimezonesDict;

        public List<int> PulledBookIds { get; } = new();

        public override PostCategory PostCategory => PostCategory.Course;

        public CoursePuller(Dictionary<int, SchoolUnique> schoolUqsDict,
            PhoenixContext phoenixContext, ILogger logger, bool verbose = true) 
            : base(schoolUqsDict, phoenixContext, logger, verbose)
        {
            this._courseRepository = new(phoenixContext);
            this._bookRepository = new(phoenixContext);
            this._schoolRepository = new(phoenixContext);

            // TODO: Check where to use
            this.schoolTimezonesDict = Task.Run(() => FindSchoolTimezonesAsync(phoenixContext, schoolUqsDict.Keys)).Result;
        }

        public override async Task<List<int>> PullAsync()
        {
            // TODO: How to update books?

            _logger.LogInformation("-----------------------------------------------------------------");
            _logger.LogInformation("Courses & Books synchronization started");

            IEnumerable<Post> coursePosts = await WPClientWrapper.GetPostsAsync(this.PostCategory);
            IEnumerable<Post> filteredPosts;

            var toCreate = new List<Course>();
            var toUpdate = new List<Course>();
            var toUpdateFrom = new List<Course>();

            var booksToCreate = new List<Book>();
            var booksToUpdate = new List<Book>(); // Used only to store the book ids

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                filteredPosts = coursePosts.FilterPostsForSchool(schoolUqPair.Value);
                
                _logger.LogInformation("{CoursesNumber} courses found for School \"{SchoolUq}\"", 
                    filteredPosts.Count(), schoolUqPair.Value.ToString());

                foreach (var coursePost in filteredPosts)
                {
                    var courseAcf = await WPClientWrapper.GetCourseAcfAsync(coursePost);
                    var courseUq = courseAcf.GetCourseUnique(schoolUqPair.Value);
                    var course = await _courseRepository.FindUniqueAsync(courseUq);

                    if (course is null)
                    {
                        if (Verbose)
                            _logger.LogInformation("Course with code {CourseCode} to be created.", courseUq.Code);

                        course = (Course)(ICourse)courseAcf;
                        course.SchoolId = schoolUqPair.Key;

                        toCreate.Add(course);
                    }
                    else
                    {
                        if (Verbose)
                            _logger.LogInformation("Course with code {CourseCode} to be updated.", courseUq.Code);

                        toUpdate.Add(course);
                        toUpdateFrom.Add((Course)(ICourse)courseAcf);
                    }

                    CourseUqsDict.Add(course.Id, courseUq);

                    if (Verbose)
                        _logger.LogInformation("Synchronizing books for course with code {CourseCode}.", courseUq.Code);

                    var courseBooks = courseAcf.Books.Cast<Book>();

                    foreach (var book in courseBooks)
                    {
                        var ctxBook = await _bookRepository.FindUniqueAsync(book.Name);
                        if (ctxBook is null)
                        {
                            if (Verbose)
                                _logger.LogInformation("Book \"{BookName}\" to be created.", book.Name);

                            booksToCreate.Add(book);
                        }
                        else
                        {
                            if (Verbose)
                                _logger.LogInformation("Book \"{BookName}\" already exists.", book.Name);

                            booksToUpdate.Add(ctxBook);

                            //ctxBook.Publisher = book.Publisher;
                            //ctxBook.Info = book.Info;
                            //bookRepository.Update(ctxBook);
                        }
                    }

                    if (Verbose)
                        _logger.LogInformation("Linking books with course with code {CourseCode}.", courseUq.Code);

                    // TODO: To check if books are linked and if old links are deleted
                    // TODO: Check if course objects affects the object in the list. If not, use that one
                    foreach (var book in courseBooks)
                        course.Books.Add(book);
                }
            }

            _logger.LogInformation("Creating {ToCreateNum} courses...", toCreate.Count);
            var created = await _courseRepository.CreateRangeAsync(toCreate);
            _logger.LogInformation("{CreatedNum}/{ToCreateNum} courses created successfully.",
                created.Count(), toCreate.Count);

            _logger.LogInformation("Creating {ToCreateNum} books...", booksToCreate.Count);
            var booksCreated = await _bookRepository.CreateRangeAsync(booksToCreate);
            _logger.LogInformation("{CreatedNum}/{ToCreateNum} books created successfully.",
                booksCreated.Count(), booksToCreate.Count);

            _logger.LogInformation("Updating {ToUpdateNum} courses...", toUpdate.Count);
            var updated = await _courseRepository.UpdateRangeAsync(toUpdate);
            var restored = await _courseRepository.RestoreRangeAsync(toUpdate);
            _logger.LogInformation("{UpdatedNum}/{ToUpdateNum} courses updated successfully.",
                updated.Count() + restored.Count(), toUpdate.Count);

            _logger.LogInformation("Courses & Books synchronization finished");
            _logger.LogInformation("-----------------------------------------------------------------");

            foreach (var course in created.Concat(updated))
                CourseUqsDict.Add(course.Id, new CourseUnique(new SchoolUnique(course.School.Code), course.Code));

            PulledBookIds.AddRange(booksCreated.Concat(booksToUpdate).Select(b => b.Id).Distinct().ToList());
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

            return ObviatedIds = await ObviateAllPerSchoolAsync(findCoursesForSchool, _courseRepository, toKeep);
        }
    }
}
