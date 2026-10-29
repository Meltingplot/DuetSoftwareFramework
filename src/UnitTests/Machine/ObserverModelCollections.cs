using DuetAPI.ObjectModel;
using DuetControlServer.Model;
using NUnit.Framework;
using System.Text.Json;

namespace UnitTests.Machine
{
    /// <summary>
    /// Tests for model collections and dictionaries that are replaced by new instances
    /// </summary>
    public partial class Observer
    {
        [Test]
        public void ObserveReplacedModelCollection()
        {
            Board board = new();
            _model.Boards.Add(board);

            StaticModelCollection<Driver> first = [];
            _changes.Clear();
            board.Drivers = first;
            AssertSingleChange("boards[0 of 1]/drivers", PropertyChangeType.Property, first);

            Driver driver = new();
            _changes.Clear();
            first.Add(driver);
            AssertSingleChange("boards[0 of 1]/drivers[0 of 1]", PropertyChangeType.Collection, driver);

            _changes.Clear();
            driver.Status = 65536;
            AssertSingleChange("boards[0 of 1]/drivers[0 of 1]/status", PropertyChangeType.Property, 65536u);

            StaticModelCollection<Driver> second = [new Driver()];
            _changes.Clear();
            board.Drivers = second;
            AssertSingleChange("boards[0 of 1]/drivers", PropertyChangeType.Property, second);

            // The replaced collection and its items must have been unsubscribed from
            _changes.Clear();
            driver.Status = 0;
            first.Add(new Driver());
            Assert.That(_changes, Is.Empty);

            second[0].Status = 1;
            AssertSingleChange("boards[0 of 1]/drivers[0 of 1]/status", PropertyChangeType.Property, 1u);
        }

        [Test]
        public void ObserveModelCollectionAfterNullUpdate()
        {
            // After a firmware reset RRF can report an expansion board without drivers before it has announced them.
            // The generated update then sets Drivers to null and later to a new collection, whose changes must be reported
            Board board = new();
            board.UpdateFromJson(JsonDocument.Parse("""{"drivers":[{"status":0}]}""").RootElement, false);
            _model.Boards.Add(board);

            board.UpdateFromJson(JsonDocument.Parse("""{"drivers":null}""").RootElement, false);
            board.UpdateFromJson(JsonDocument.Parse("""{"drivers":[{"status":0}]}""").RootElement, false);

            _changes.Clear();
            board.Drivers![0].Status = 65536;
            AssertSingleChange("boards[0 of 1]/drivers[0 of 1]/status", PropertyChangeType.Property, 65536u);
        }
    }
}
